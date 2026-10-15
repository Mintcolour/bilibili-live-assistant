using System.Diagnostics;
using System.Text.Json;
using BilibiliLiveAssistant.Core;
using Microsoft.Data.Sqlite;

namespace BilibiliLiveAssistant.Infrastructure;

public sealed partial class SqliteLocalHistory
{
    private readonly object _sync = new();
    private readonly Queue<(HistoryEnvelope Item, int Bytes)> _ordinary = new(), _important = new();
    private readonly Queue<Work> _commands = new();
    private readonly Dictionary<(string Session, LiveEventType Type, string Day), (HistoryContext Context, HistoryGap Gap)> _gaps = new();
    private readonly SemaphoreSlim _wake = new(0, 1), _readGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;
    private readonly Timer _snapshots;
    private HistorySettings _settings;
    private string _directory;
    private int _bytes, _ordinaryBytes;
    private int _inflightCount, _inflightBytes, _inflightOrdinaryBytes, _inflightOrdinaryCount;
    private long _dropped;
    private bool _closing;
    private HistoryContext? _current;
    private LiveRoomProfile? _profile;
    private IReadOnlyList<LiveRankMember>? _rank;
    private long? _popularity, _watchedCount, _rankTotal;
    private bool _hasObservation;
    private bool _observationDirty;
    private DateTimeOffset _observationAt;
    private HistoryStatus _status = new("正在打开历史存档", 0, 0, 0, false, false);
    private sealed record Work(Action<SqliteConnection> Run, TaskCompletionSource Completion, bool Drain);
    public event Action<HistoryStatus>? StatusChanged;
    public event Action? Committed;
    public HistoryStatus Status => Volatile.Read(ref _status);
    public string DatabasePath => Path.Combine(Volatile.Read(ref _directory), "history.db");
    public SqliteLocalHistory(HistorySettings settings)
    {
        _settings = settings.Normalize(); _directory = _settings.EffectiveDirectory;
        _snapshots = new Timer(_ => SaveObservation(), null, Timeout.Infinite, Timeout.Infinite);
        _worker = Task.Run(WriterLoopAsync);
    }
    public void Configure(HistorySettings settings)
    {
        HistoryContext? context = null;
        lock (_sync)
        {
            var enabled = !_settings.Enabled && settings.Enabled; _settings = settings.Normalize();
            if (enabled) context = _current;
            _snapshots.Change(_current is not null && _settings.Enabled ? 60000 : Timeout.Infinite, _settings.Enabled ? 60000 : Timeout.Infinite);
        }
        if (context is not null) ObserveControl(QueueCommand(c => EnsureSession(c, context), false));
        Publish(Status.Faulted ? Status.Message : "历史存档就绪", Status.Faulted);
    }
    public HistoryContext Begin(int roomId)
    {
        var context = new HistoryContext(Guid.NewGuid().ToString("N"), roomId, DateTimeOffset.UtcNow);
        lock (_sync)
        {
            _current = context; _profile = null; _rank = null; _popularity = _watchedCount = _rankTotal = null; _hasObservation = _observationDirty = false;
            _snapshots.Change(_settings.Enabled ? 60000 : Timeout.Infinite, _settings.Enabled ? 60000 : Timeout.Infinite);
        }
        if (_settings.Enabled) ObserveControl(QueueCommand(c => EnsureSession(c, context), false)); return context;
    }
    public void End(HistoryContext context)
    {
        lock (_sync) { if (_current?.Id == context.Id) { SaveObservationLocked(); _current = null; _snapshots.Change(Timeout.Infinite, Timeout.Infinite); } }
        var ended = DateTimeOffset.UtcNow;
        ObserveControl(QueueCommand(c => Execute(c, "UPDATE sessions SET ended=$at,status='stopped',heartbeat=MAX(heartbeat,$at),last_day=MAX(last_day,$day) WHERE id=$id", ("$at", Millis(ended)), ("$day", Day(ended)), ("$id", context.Id)), false));
    }
    public void Capture(HistoryContext context, LiveEvent value, DateTimeOffset receivedAt)
    {
        lock (_sync)
        {
            if (_closing || !_settings.Enabled) return;
            if (value.Extra.GetValueOrDefault("archive_analysis") is HistoryAnalysisSample)
            { if (_current?.Id == context.Id) EnqueueLocked(new(context, receivedAt, Day(receivedAt), value, new())); return; }
            if (value.Type is LiveEventType.Online or LiveEventType.Rank)
            {
                if (_current?.Id != context.Id) return;
                _observationDirty = true; _observationAt = receivedAt;
                if (value.Extra.TryGetValue("archive_popularity", out var p)) _popularity = p as long?;
                if (value.Extra.TryGetValue("archive_watched", out var w)) _watchedCount = w as long?;
                if (value.Type == LiveEventType.Rank)
                {
                    _rank = value.Extra.GetValueOrDefault("rank_members") as IReadOnlyList<LiveRankMember> ?? Array.Empty<LiveRankMember>();
                    _rankTotal = value.Extra.GetValueOrDefault("rank_total") as long?;
                }
                if (value.Extra.GetValueOrDefault("room_profile") is LiveRoomProfile profile && profile != _profile)
                {
                    _profile = profile;
                    var profileEvent = new LiveEvent(LiveEventType.Online, "系统", "", "#FFFFFF", Metadata: new Dictionary<string, object?>
                        { ["room_profile"] = profile, ["archive_profile_only"] = true });
                    EnqueueLocked(new(context, receivedAt, Day(receivedAt), profileEvent, new()));
                }
                if (!_hasObservation) { _hasObservation = true; SaveObservationLocked(); }
                return;
            }
            EnqueueLocked(new(context, receivedAt, Day(receivedAt), value with { Metadata = null, MediaReferences = Array.Empty<MediaReference>() }, value.HistoryFields ?? new()));
        }
    }
    private void SaveObservation()
    {
        HistoryContext? context; var at = DateTimeOffset.UtcNow;
        lock (_sync) { if (_closing || !_settings.Enabled) return; context = _current; SaveObservationLocked(); }
        if (context is not null) ObserveControl(QueueCommand(c =>
            Execute(c, "UPDATE sessions SET heartbeat=MAX(heartbeat,$at),last_day=MAX(last_day,$day) WHERE id=$id", ("$at", Millis(at)), ("$day", Day(at)), ("$id", context.Id)), false));
    }
    private void SaveObservationLocked()
    {
        if (_current is not { } context || !_settings.Enabled || !_hasObservation || !_observationDirty || _closing) return;
        var at = _observationAt; _observationDirty = false;
        var value = new LiveEvent(LiveEventType.Online, "系统", "", "#FFFFFF", Metadata: new Dictionary<string, object?>
        { ["archive_popularity"] = _popularity, ["archive_watched"] = _watchedCount, ["rank_members"] = _rank, ["rank_total"] = _rankTotal });
        EnqueueLocked(new(context, at, Day(at), value, new()));
    }
    private static int Estimate(HistoryEnvelope item)
    {
        var value = item.Event; var a = value.Appearance;
        long bytes = 2048L + 2L * (value.Username.Length + value.Content.Length + value.Color.Length);
        var fields = item.Fields;
        if (value.Extra.GetValueOrDefault("archive_analysis") is HistoryAnalysisSample sample)
            foreach (var member in sample.Members) bytes += 768 + 2L * (member.Username.Length + member.Appearance.AvatarUrl.Length + member.Appearance.MedalName.Length);
        bytes += 2L * ((fields.RawBody?.Length ?? 0) + (fields.TransactionId?.Length ?? 0) + (fields.GiftName?.Length ?? 0) + (fields.UniqueId?.Length ?? 0));
        if (value.Extra.GetValueOrDefault("room_profile") is LiveRoomProfile profile)
            bytes += 2L * (profile.Name.Length + profile.Title.Length + profile.AvatarUrl.Length + profile.MedalName.Length);
        if (a is not null)
        {
            bytes += 2L * (a.AvatarUrl.Length + a.AvatarFrameUrl.Length + a.MedalName.Length + a.InteractionIconUrl.Length + a.InteractionHint.Length);
            foreach (var emote in a.Emotes) bytes += 160 + 2L * (emote.Token.Length + emote.Url.Length);
            if (a.FullEmote is { } full) bytes += 160 + 2L * (full.Token.Length + full.Url.Length);
            if (a.EntryEffect is { } effect) bytes += 256 + 2L * (effect.BackgroundUrl.Length + effect.BadgeUrl.Length);
        }
        if (value.Extra.GetValueOrDefault("rank_members") is IReadOnlyList<LiveRankMember> members)
            foreach (var member in members)
                bytes += 512L + 2L * (member.Username.Length + member.Appearance.AvatarUrl.Length + member.Appearance.AvatarFrameUrl.Length + member.Appearance.MedalName.Length);
        return (int)Math.Min(int.MaxValue, bytes);
    }
    private void EnqueueLocked(HistoryEnvelope item)
    {
        var important = item.Event.Type is LiveEventType.Gift or LiveEventType.SuperChat or LiveEventType.Guard;
        var bytes = Estimate(item); var count = _ordinary.Count + _important.Count + _inflightCount;
        if (count >= 8192 || _bytes + bytes > 16 * 1024 * 1024 || !important && (_ordinary.Count + _inflightOrdinaryCount >= 7168 || _ordinaryBytes + bytes > 14 * 1024 * 1024))
        { AddGapLocked(item, "写入缓冲已满"); Wake(); return; }
        (important ? _important : _ordinary).Enqueue((item, bytes)); _bytes += bytes; if (!important) _ordinaryBytes += bytes;
        if (_ordinary.Count + _important.Count is 1 or 500) Wake();
    }
    private void AddGapLocked(HistoryEnvelope item, string reason)
    {
        _dropped++; var key = (item.Context.Id, item.Event.Type, item.LocalDay);
        if (_gaps.TryGetValue(key, out var previous))
            _gaps[key] = (previous.Context, previous.Gap with { Count = previous.Gap.Count + 1, FirstAt = previous.Gap.FirstAt < item.ReceivedAt ? previous.Gap.FirstAt : item.ReceivedAt,
                LastAt = previous.Gap.LastAt > item.ReceivedAt ? previous.Gap.LastAt : item.ReceivedAt });
        else if (_gaps.Count < 256)
            _gaps[key] = (item.Context, new(item.Context.Id, item.Context.RoomId, item.Event.Type, 1, item.ReceivedAt, item.ReceivedAt, reason));
        else
        {
            // Bounded exceptional fallback is explicitly unassigned rather than attributed to the wrong room.
            var fallback = ("", item.Event.Type, "*");
            if (_gaps.TryGetValue(fallback, out var overflow)) _gaps[fallback] = (overflow.Context, overflow.Gap with { Count = overflow.Gap.Count + 1, LastAt = item.ReceivedAt });
            else if (_gaps.Count < 272) _gaps[fallback] = (item.Context, new(null, 0, item.Event.Type, 1, item.ReceivedAt, item.ReceivedAt, "多个房间拥堵，未归属缺口"));
        }
    }
    private void Wake() { if (_wake.CurrentCount != 0) return; try { _wake.Release(); } catch (SemaphoreFullException) { } }
    private void ObserveControl(Task task) => _ = task.ContinueWith(t => Publish("存档记录状态写入失败：" + t.Exception?.GetBaseException().Message, true),
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    private Task QueueCommand(Action<SqliteConnection> run, bool drain = true)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_commands.Count >= 128 || _closing || drain && Status.Faulted) { completion.SetException(new IOException("存档后台繁忙或存储故障，请修复后重试")); return completion.Task; }
            _commands.Enqueue(new(run, completion, drain));
        }
        Wake(); return completion.Task;
    }
    private async Task WriterLoopAsync()
    {
        SqliteConnection? connection = null; FileStream? ownership = null; var recovered = false;
        var batch = new List<HistoryEnvelope>(500);
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    if (connection is not null && !string.Equals(connection.DataSource, DatabasePath, StringComparison.OrdinalIgnoreCase))
                    { connection.Dispose(); connection = null; ownership?.Dispose(); ownership = null; }
                    if (connection is null)
                    {
                        Directory.CreateDirectory(_directory);
                        ownership = new FileStream(Path.Combine(_directory, "history.writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                        connection = Open(); Initialize(connection, !recovered);
                        if (!recovered) RecoverJournal(connection);
                        recovered = true; _ready.TrySetResult(); Publish("历史存档就绪", false);
                    }
                    bool hasData, hasCommands;
                    lock (_sync) { hasData = _important.Count + _ordinary.Count + _gaps.Count > 0; hasCommands = _commands.Count > 0; }
                    if (!hasData && !hasCommands) { await _wake.WaitAsync(_shutdown.Token); continue; }
                    // Coalesce for up to a second, without any idle timer or polling.
                    var first = Stopwatch.GetTimestamp();
                    while (!hasCommands)
                    {
                        int pending; lock (_sync) { pending = _important.Count + _ordinary.Count; hasCommands = _commands.Count > 0; }
                        var remaining = TimeSpan.FromSeconds(1) - Stopwatch.GetElapsedTime(first);
                        if (pending >= 500 || remaining <= TimeSpan.Zero || hasCommands) break;
                        await _wake.WaitAsync(remaining, _shutdown.Token);
                    }
                    batch.Clear(); (HistoryContext Context, HistoryGap Gap)[] gaps;
                    lock (_sync)
                    {
                        while (batch.Count < 500 && (_important.Count + _ordinary.Count > 0))
                        {
                            // Reserved admission protects gifts; merge queue heads by receipt time for truthful member changes.
                            var important = _important.Count > 0 && (_ordinary.Count == 0 || _important.Peek().Item.ReceivedAt <= _ordinary.Peek().Item.ReceivedAt);
                            var entry = (important ? _important : _ordinary).Dequeue();
                            _inflightBytes += entry.Bytes; if (!important) { _inflightOrdinaryBytes += entry.Bytes; _inflightOrdinaryCount++; }
                            batch.Add(entry.Item);
                        }
                        gaps = _gaps.Values.ToArray(); _gaps.Clear();
                        _inflightCount = batch.Count;
                    }
                    if (batch.Count > 0 || gaps.Length > 0)
                    {
                        try
                        {
                            using var transaction = new HistoryTransaction(connection);
                            foreach (var item in batch) WriteEnvelope(connection, item);
                            foreach (var entry in gaps) { EnsureSession(connection, entry.Context); WriteGap(connection, entry.Gap); }
                            transaction.Commit(); Committed?.Invoke();
                            TryRemoveJournal();
                        }
                        catch
                        {
                            _medalStates.Clear();
                            lock (_sync)
                            {
                                foreach (var item in batch) AddGapLocked(item, "数据库写入失败");
                                foreach (var entry in gaps) MergeGapLocked(entry);
                            }
                            throw;
                        }
                        finally
                        {
                            lock (_sync) { _bytes -= _inflightBytes; _ordinaryBytes -= _inflightOrdinaryBytes; _inflightCount = _inflightBytes = _inflightOrdinaryBytes = _inflightOrdinaryCount = 0; }
                        }
                    }
                    Work? work = null;
                    lock (_sync) if (_commands.Count > 0 && (!_commands.Peek().Drain || _important.Count + _ordinary.Count == 0)) work = _commands.Dequeue();
                    if (work is not null)
                    {
                        try { work.Run(connection); work.Completion.TrySetResult(); Committed?.Invoke(); }
                        catch (Exception ex) when (ex is not OutOfMemoryException)
                        {
                            work.Completion.TrySetException(ex);
                            if (ex is SqliteException sqlite && sqlite.SqliteErrorCode is 10 or 13 or 14) throw;
                        }
                    }
                    Publish(_dropped > 0 ? "存档有遗漏，请在历史记录查看缺口" : "历史存档就绪", false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    connection?.Dispose(); connection = null; ownership?.Dispose(); ownership = null;
                    Publish("归档异常：" + ex.Message + "；直播监视继续，5 秒后重试", true);
                    lock (_sync)
                    {
                        var controls = new List<Work>();
                        while (_commands.TryDequeue(out var pending))
                            if (pending.Drain) pending.Completion.TrySetException(new IOException("存档存储故障，操作未完成", ex)); else controls.Add(pending);
                        foreach (var pending in controls) _commands.Enqueue(pending);
                    }
                    PersistJournal();
                    await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { PersistJournal(); connection?.Dispose(); ownership?.Dispose(); _ready.TrySetCanceled(); }
    }
    private void MergeGapLocked((HistoryContext Context, HistoryGap Gap) entry)
    {
        var key = (entry.Gap.SessionId ?? "", entry.Gap.Type, entry.Gap.SessionId is null ? "*" : Day(entry.Gap.FirstAt));
        if (_gaps.TryGetValue(key, out var prior)) _gaps[key] = (entry.Context, prior.Gap with { Count = prior.Gap.Count + entry.Gap.Count,
            FirstAt = prior.Gap.FirstAt < entry.Gap.FirstAt ? prior.Gap.FirstAt : entry.Gap.FirstAt, LastAt = prior.Gap.LastAt > entry.Gap.LastAt ? prior.Gap.LastAt : entry.Gap.LastAt });
        else if (_gaps.Count < 272) _gaps[key] = entry;
    }
    private sealed record JournalEntry(HistoryContext Context, HistoryGap Gap);
    private sealed record GapJournal(int ProcessId, long ProcessStarted, JournalEntry[] Entries);
    private readonly string _journalName = $"pending-gaps-{Environment.ProcessId}-{Guid.NewGuid():N}.json";
    private readonly long _processStarted = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    private string JournalPath => Path.Combine(_directory, _journalName);
    private void PersistJournal()
    {
        try
        {
            JournalEntry[] entries;
            lock (_sync)
            {
                entries = _gaps.Values.Select(g => new JournalEntry(g.Context, g.Gap)).ToArray();
                if (_shutdown.IsCancellationRequested)
                {
                    foreach (var entry in _ordinary.Concat(_important)) AddGapLocked(entry.Item, "退出落盘超时");
                    entries = _gaps.Values.Select(g => new JournalEntry(g.Context, g.Gap)).ToArray();
                    _ordinary.Clear(); _important.Clear(); _bytes = _ordinaryBytes = 0;
                }
            }
            if (entries.Length == 0) return;
            Directory.CreateDirectory(_directory);
            File.WriteAllText(JournalPath + ".tmp", JsonSerializer.Serialize(new GapJournal(Environment.ProcessId, _processStarted, entries), Json)); File.Move(JournalPath + ".tmp", JournalPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Status already exposes the disk fault; no credential or packet spill. */ }
    }
    private void RecoverJournal(SqliteConnection connection)
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "pending-gaps-*.json").Take(64))
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("存档缺口恢复文件过大，请保留备份并检查");
            var journal = JsonSerializer.Deserialize<GapJournal>(File.ReadAllText(path), Json);
            if (journal is null) continue;
            var own = string.Equals(path, JournalPath, StringComparison.OrdinalIgnoreCase);
            if (!own && JournalOwnerRunning(journal)) continue;
            var entries = journal.Entries.Take(272).ToArray();
            using var transaction = new HistoryTransaction(connection);
            foreach (var entry in entries)
            {
                EnsureSession(connection, entry.Context); WriteGap(connection, entry.Gap);
                if (_current?.Id != entry.Context.Id)
                    Execute(connection, "UPDATE sessions SET status='interrupted',ended=COALESCE(ended,$at) WHERE id=$id", ("$at", Millis(entry.Gap.LastAt)), ("$id", entry.Context.Id));
            }
            transaction.Commit(); TryRemoveJournal(path);
            if (!own) lock (_sync) _dropped += entries.Sum(e => e.Gap.Count);
        }
    }
    private static bool JournalOwnerRunning(GapJournal journal)
    {
        try
        {
            using var process = Process.GetProcessById(journal.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == journal.ProcessStarted;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
    private void TryRemoveJournal(string? path = null) { try { path ??= JournalPath; if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private void Publish(string message, bool faulted)
    {
        long bytes = 0; foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
        { try { if (File.Exists(path)) bytes += new FileInfo(path).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        HistoryStatus next;
        lock (_sync) next = new(message, _dropped, bytes, _ordinary.Count + _important.Count + _inflightCount, faulted, bytes >= _settings.CapacityWarningGiB * 1024L * 1024 * 1024);
        var prior = Interlocked.Exchange(ref _status, next);
        if (prior != next) StatusChanged?.Invoke(next);
    }
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        try { await QueueCommand(_ => { }).WaitAsync(timeout).ConfigureAwait(false); return true; }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException) { return false; }
    }
    public async ValueTask DisposeAsync()
    {
        _snapshots.Change(Timeout.Infinite, Timeout.Infinite);
        if (_current is { } current) End(current);
        var deadline = Stopwatch.GetTimestamp();
        var flushed = await FlushAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        lock (_sync) _closing = true;
        if (!flushed) Publish("退出时未完成落盘；下次启动显示中断和可恢复的遗漏范围", true);
        _shutdown.Cancel(); Wake();
        try { await _worker.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(1, (TimeSpan.FromSeconds(5) - Stopwatch.GetElapsedTime(deadline)).TotalMilliseconds))).ConfigureAwait(false); }
        catch (TimeoutException) { Publish("退出时存档提交超时，下次启动标记中断", true); }
        if (!flushed) Publish("退出时未确认全部落盘；下次启动显示中断和可恢复的遗漏范围", true);
        // SQLite busy timeout is bounded; do not add another unbounded UI shutdown wait.
        _ = _worker.ContinueWith(_ => { _snapshots.Dispose(); _shutdown.Dispose(); }, TaskScheduler.Default);
    }
}
