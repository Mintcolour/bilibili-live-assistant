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
    private long _dropped;
    private bool _closing;
    private HistoryContext? _current;
    private LiveRoomProfile? _profile;
    private IReadOnlyList<LiveRankMember>? _rank;
    private long? _popularity, _watchedCount, _rankTotal;
    private bool _hasObservation;
    private HistoryStatus _status = new("正在打开历史存档", 0, 0, 0, false, false);
    private sealed record Work(Action<SqliteConnection> Run, TaskCompletionSource Completion);
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
    public void Configure(HistorySettings settings) { lock (_sync) _settings = settings.Normalize(); Publish(Status.Faulted ? Status.Message : "历史存档就绪", Status.Faulted); }
    public HistoryContext Begin(int roomId)
    {
        var context = new HistoryContext(Guid.NewGuid().ToString("N"), roomId, DateTimeOffset.UtcNow);
        lock (_sync)
        {
            _current = context; _profile = null; _rank = null; _popularity = _watchedCount = _rankTotal = null; _hasObservation = false;
            _snapshots.Change(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }
        QueueCommand(c => EnsureSession(c, context)); return context;
    }
    public void End(HistoryContext context)
    {
        lock (_sync) { if (_current?.Id == context.Id) { SaveObservationLocked(); _current = null; _snapshots.Change(Timeout.Infinite, Timeout.Infinite); } }
        var ended = DateTimeOffset.UtcNow;
        QueueCommand(c => { EnsureSession(c, context); Execute(c, "UPDATE sessions SET ended=$at,status='stopped' WHERE id=$id", ("$at", Millis(ended)), ("$id", context.Id)); });
    }
    public void Capture(HistoryContext context, LiveEvent value, DateTimeOffset receivedAt)
    {
        lock (_sync)
        {
            if (_closing || !_settings.Enabled) return;
            if (value.Type is LiveEventType.Online or LiveEventType.Rank)
            {
                if (_current?.Id != context.Id) return;
                if (value.Extra.TryGetValue("archive_popularity", out var p) && p is long pv) _popularity = pv;
                if (value.Extra.TryGetValue("archive_watched", out var w) && w is long wv) _watchedCount = wv;
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
            EnqueueLocked(new(context, receivedAt, Day(receivedAt), value, value.HistoryFields ?? new()));
        }
    }
    private void SaveObservation() { lock (_sync) SaveObservationLocked(); }
    private void SaveObservationLocked()
    {
        if (_current is not { } context || !_settings.Enabled || !_hasObservation || _closing) return;
        var at = DateTimeOffset.UtcNow;
        var value = new LiveEvent(LiveEventType.Online, "系统", "", "#FFFFFF", Metadata: new Dictionary<string, object?>
        { ["archive_popularity"] = _popularity, ["archive_watched"] = _watchedCount, ["rank_members"] = _rank, ["rank_total"] = _rankTotal });
        EnqueueLocked(new(context, at, Day(at), value, new()));
    }
    private static int Estimate(HistoryEnvelope item)
    {
        var value = item.Event; var a = value.Appearance;
        long bytes = 2048L + 2L * (value.Username.Length + value.Content.Length + value.Color.Length);
        if (a is not null)
        {
            bytes += 2L * (a.AvatarUrl.Length + a.AvatarFrameUrl.Length + a.MedalName.Length + a.InteractionIconUrl.Length + a.InteractionHint.Length);
            foreach (var emote in a.Emotes) bytes += 160 + 2L * (emote.Token.Length + emote.Url.Length);
            if (a.FullEmote is { } full) bytes += 160 + 2L * (full.Token.Length + full.Url.Length);
            if (a.EntryEffect is { } effect) bytes += 256 + 2L * (effect.BackgroundUrl.Length + effect.BadgeUrl.Length);
        }
        bytes += 512L * (value.Extra.GetValueOrDefault("rank_members") is IReadOnlyList<LiveRankMember> members ? members.Count : 0);
        return (int)Math.Min(int.MaxValue, bytes);
    }
    private void EnqueueLocked(HistoryEnvelope item)
    {
        var important = item.Event.Type is LiveEventType.Gift or LiveEventType.SuperChat or LiveEventType.Guard;
        var bytes = Estimate(item); var count = _ordinary.Count + _important.Count;
        if (count >= 8192 || _bytes + bytes > 16 * 1024 * 1024 || !important && (_ordinary.Count >= 7168 || _ordinaryBytes + bytes > 14 * 1024 * 1024))
        { AddGapLocked(item, "写入缓冲已满"); Wake(); return; }
        (important ? _important : _ordinary).Enqueue((item, bytes)); _bytes += bytes; if (!important) _ordinaryBytes += bytes;
        Wake();
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
            var fallback = ("", item.Event.Type, item.LocalDay);
            if (_gaps.TryGetValue(fallback, out var overflow)) _gaps[fallback] = (overflow.Context, overflow.Gap with { Count = overflow.Gap.Count + 1, LastAt = item.ReceivedAt });
            else if (_gaps.Count < 272) _gaps[fallback] = (item.Context, new(null, 0, item.Event.Type, 1, item.ReceivedAt, item.ReceivedAt, "多个房间拥堵，未归属缺口"));
        }
    }
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } }
    private Task QueueCommand(Action<SqliteConnection> run)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (_commands.Count >= 128 || _closing) { completion.SetException(new IOException("存档后台操作繁忙")); return completion.Task; }
            _commands.Enqueue(new(run, completion));
        }
        Wake(); return completion.Task;
    }
    private async Task WriterLoopAsync()
    {
        SqliteConnection? connection = null; var recovered = false;
        var batch = new List<HistoryEnvelope>(500);
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                try
                {
                    if (connection is not null && !string.Equals(connection.DataSource, DatabasePath, StringComparison.OrdinalIgnoreCase))
                    { connection.Dispose(); connection = null; }
                    if (connection is null)
                    {
                        Directory.CreateDirectory(_directory); connection = Open(); Initialize(connection, !recovered);
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
                            var important = _important.Count > 0;
                            var entry = (important ? _important : _ordinary).Dequeue();
                            _bytes -= entry.Bytes; if (!important) _ordinaryBytes -= entry.Bytes;
                            batch.Add(entry.Item);
                        }
                        gaps = _gaps.Values.ToArray(); _gaps.Clear();
                    }
                    if (batch.Count > 0 || gaps.Length > 0)
                    {
                        try
                        {
                            using var transaction = connection.BeginTransaction();
                            foreach (var item in batch) WriteEnvelope(connection, item);
                            foreach (var entry in gaps) { EnsureSession(connection, entry.Context); WriteGap(connection, entry.Gap); }
                            transaction.Commit(); Committed?.Invoke();
                            TryRemoveJournal();
                        }
                        catch
                        {
                            lock (_sync)
                            {
                                foreach (var item in batch) AddGapLocked(item, "数据库写入失败");
                                foreach (var entry in gaps) MergeGapLocked(entry);
                            }
                            throw;
                        }
                    }
                    Work? work = null;
                    lock (_sync) if (_important.Count + _ordinary.Count == 0 && _commands.Count > 0) work = _commands.Dequeue();
                    if (work is not null)
                    {
                        try { work.Run(connection); work.Completion.TrySetResult(); }
                        catch (Exception ex) when (ex is not OutOfMemoryException) { work.Completion.TrySetException(ex); }
                    }
                    Publish(_dropped > 0 ? "存档有遗漏，请在历史记录查看缺口" : "历史存档就绪", false);
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    connection?.Dispose(); connection = null;
                    Publish("归档异常：" + ex.Message + "；直播监视继续，5 秒后重试", true);
                    PersistJournal();
                    await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { PersistJournal(); connection?.Dispose(); _ready.TrySetCanceled(); }
    }
    private void MergeGapLocked((HistoryContext Context, HistoryGap Gap) entry)
    {
        var key = (entry.Gap.SessionId ?? "", entry.Gap.Type, Day(entry.Gap.FirstAt));
        if (_gaps.TryGetValue(key, out var prior)) _gaps[key] = (entry.Context, prior.Gap with { Count = prior.Gap.Count + entry.Gap.Count,
            FirstAt = prior.Gap.FirstAt < entry.Gap.FirstAt ? prior.Gap.FirstAt : entry.Gap.FirstAt, LastAt = prior.Gap.LastAt > entry.Gap.LastAt ? prior.Gap.LastAt : entry.Gap.LastAt });
        else if (_gaps.Count < 272) _gaps[key] = entry;
    }
    private sealed record JournalEntry(HistoryContext Context, HistoryGap Gap);
    private string JournalPath => Path.Combine(_directory, "pending-gaps.json");
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
            File.WriteAllText(JournalPath + ".tmp", JsonSerializer.Serialize(entries, Json)); File.Move(JournalPath + ".tmp", JournalPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Status already exposes the disk fault; no credential or packet spill. */ }
    }
    private void RecoverJournal(SqliteConnection connection)
    {
        if (!File.Exists(JournalPath)) return;
        if (new FileInfo(JournalPath).Length > 1024 * 1024) throw new InvalidDataException("存档缺口恢复文件过大，请保留备份并检查");
        var entries = JsonSerializer.Deserialize<JournalEntry[]>(File.ReadAllText(JournalPath), Json) ?? [];
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries.Take(272))
        {
            EnsureSession(connection, entry.Context); WriteGap(connection, entry.Gap);
            Execute(connection, "UPDATE sessions SET status='interrupted',ended=COALESCE(ended,$at) WHERE id=$id", ("$at", Millis(entry.Gap.LastAt)), ("$id", entry.Context.Id));
        }
        transaction.Commit(); TryRemoveJournal();
        lock (_sync)
        {
            // These entries are the previous process's journal, not this process's in-memory losses.
            _dropped += entries.Sum(e => e.Gap.Count);
        }
    }
    private void TryRemoveJournal() { try { if (File.Exists(JournalPath)) File.Delete(JournalPath); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private void Publish(string message, bool faulted)
    {
        long bytes = 0; foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
        { try { if (File.Exists(path)) bytes += new FileInfo(path).Length; } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        HistoryStatus next;
        lock (_sync) next = new(message, _dropped, bytes, _ordinary.Count + _important.Count, faulted, bytes >= _settings.CapacityWarningGiB * 1024L * 1024 * 1024);
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
        var flushed = await FlushAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        lock (_sync) _closing = true;
        if (!flushed) Publish("退出时未完成落盘；下次启动显示中断和可恢复的遗漏范围", true);
        _shutdown.Cancel(); Wake();
        // SQLite busy timeout is bounded; do not add another unbounded UI shutdown wait.
        _ = _worker.ContinueWith(_ => { _snapshots.Dispose(); _shutdown.Dispose(); }, TaskScheduler.Default);
    }
}
