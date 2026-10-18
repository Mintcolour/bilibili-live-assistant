using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BilibiliLiveAssistant.Core;
using Microsoft.Data.Sqlite;

namespace BilibiliLiveAssistant.App;

// Diagnostic transport only: a consistent copy of an isolated observation, not
// the user's history. Stream rows, never retain a whole chat in the target heap.
internal sealed class ArchivedReplay
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _database, _images, _session;
    private readonly long _from, _to;
    private readonly double _speed;
    private TimeSpan _profileTimeShift;
    private long _cycles, _events, _missingImages, _loadedImages, _skippedOffline, _mediaReferences;
    private readonly long _fixtureBytes;
    private readonly int _fixtureFiles;
    private DashboardRankPage _rank = new([], false, null);
    internal int Room { get; }
    internal ArchivedReplay(string sourceDirectory, string outputDirectory, int windowSeconds, string[]? imageSources, double speed)
    {
        if (!double.IsFinite(speed) || speed is < 0.25 or > 20) throw new ArgumentOutOfRangeException(nameof(speed));
        _speed = speed;
        var source = Path.GetFullPath(sourceDirectory);
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..")) + Path.DirectorySeparatorChar;
        if (!source.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(source, "samples.jsonl")))
            throw new InvalidOperationException("回放只接受本机 memory-soak 下的隔离观察目录");
        _database = Path.Combine(outputDirectory, "replay-source.db");
        _images = Path.Combine(outputDirectory, "replay-images");
        Directory.CreateDirectory(_images);
        using (var original = Open(Path.Combine(source, "history", "history.db")))
        using (var copy = Open(_database, false)) original.BackupDatabase(copy);
        using var connection = Open(_database);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,room FROM sessions WHERE EXISTS(SELECT 1 FROM events WHERE session_id=sessions.id) ORDER BY started DESC LIMIT 1";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidDataException("隔离观察没有可回放记录");
            _session = reader.GetString(0); Room = reader.GetInt32(1);
        }
        command.CommandText = "SELECT MIN(received),MAX(received) FROM events WHERE session_id=$s";
        command.Parameters.AddWithValue("$s", _session);
        using (var reader = command.ExecuteReader())
        {
            reader.Read(); _to = reader.GetInt64(1);
            _from = Math.Max(reader.GetInt64(0), _to - windowSeconds * 1000L);
        }
        if (_to - _from < 30000) throw new InvalidDataException("回放至少需要 30 秒真实消息");
        foreach (var folder in new[] { source }.Concat(imageSources ?? []).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
        {
            if (!folder.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("补充图片只接受隔离观察目录");
            using var headerStream = new FileStream(Path.Combine(folder, "samples.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var headerReader = new StreamReader(headerStream);
            using var header = JsonDocument.Parse(headerReader.ReadLine() ?? throw new InvalidDataException("隔离观察头部缺失"));
            if (header.RootElement.GetProperty("config").GetProperty("LiveRoom").GetInt32() != Room)
                throw new InvalidOperationException("补充图片必须来自同一房间的隔离观察");
            foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "images"), "*.img").Take(4096))
            {
                if (_fixtureFiles >= 4096) break;
                try
                {
                    var destination = Path.Combine(_images, Path.GetFileName(file));
                    var size = new FileInfo(file).Length;
                    if (File.Exists(destination) || size > 2 * 1024 * 1024 || _fixtureBytes + size > 128L * 1024 * 1024 || _fixtureFiles >= 4096) continue;
                    File.Copy(file, destination, false); _fixtureBytes += size; _fixtureFiles++;
                }
                catch (FileNotFoundException) { } // Concurrent source cache eviction.
            }
        }
    }
    private static SqliteConnection Open(string path, bool readOnly = true)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA cache_size=-256; PRAGMA temp_store=FILE;"; command.ExecuteNonQuery();
        return connection;
    }
    internal HttpMessageHandler ImageHandler() => new CachedImages(this);
    internal object Diagnostics() => new
    {
        sourceDurationSeconds = (_to - _from) / 1000d, speed = _speed, cycles = Interlocked.Read(ref _cycles),
        emitted = Interlocked.Read(ref _events), loadedImages = Interlocked.Read(ref _loadedImages),
        fixtureFiles = _fixtureFiles, fixtureBytes = _fixtureBytes,
        missingImages = Interlocked.Read(ref _missingImages), skippedOffline = Interlocked.Read(ref _skippedOffline),
        mediaReferencesWithoutOfflineDetails = Interlocked.Read(ref _mediaReferences),
        note = "Local replay of received events, not a live stream; fixed identities and cached images are reused each cycle."
    };
    internal DashboardRankPage RankPage(int page)
    {
        var rank = Volatile.Read(ref _rank);
        return new(page == 1 ? rank.Members : [], false, rank.Total);
    }
    internal async Task RunAsync(int seconds, Action<LiveEvent> emit, CancellationToken token)
    {
        // One replay represents one broadcast, including across repeated clips.
        // Shifting each profile by its delivery time would invent new starts.
        _profileTimeShift = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(_from);
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < seconds)
        {
            var cycle = Interlocked.Increment(ref _cycles);
            var cycleStart = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            using var connection = Open(_database);
            // Prime room/rank state immediately, then replay every change in time order.
            using (var initial = Command(connection, """
                SELECT 1,id,received,0,0,'','','',NULL,NULL,data,NULL,NULL,NULL,NULL FROM profiles
                WHERE session_id=$s AND received<=$from ORDER BY received DESC LIMIT 1
                """))
            using (var reader = initial.ExecuteReader()) if (reader.Read()) Emit(reader, cycle, emit);
            using (var initial = Command(connection, """
                SELECT 2,id,received,0,0,'','','',NULL,NULL,NULL,popularity,watched,rank,rank_total FROM snapshots
                WHERE session_id=$s AND received<=$from ORDER BY received DESC LIMIT 1
                """))
            using (var reader = initial.ExecuteReader()) if (reader.Read()) Emit(reader, cycle, emit);
            using var command = Command(connection, """
                SELECT 0 AS kind,id,received,type,uid,name,content,color,appearance,fields,NULL AS data,NULL AS popularity,NULL AS watched,NULL AS rank,NULL AS rank_total
                FROM events WHERE session_id=$s AND received>=$from AND received<=$to
                UNION ALL
                SELECT 1,id,received,0,0,'','','',NULL,NULL,data,NULL,NULL,NULL,NULL
                FROM profiles WHERE session_id=$s AND received>$from AND received<=$to
                UNION ALL
                SELECT 2,id,received,0,0,'','','',NULL,NULL,NULL,popularity,watched,rank,rank_total
                FROM snapshots WHERE session_id=$s AND received>$from AND received<=$to ORDER BY received,kind,id
                """);
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                var targetMs = cycleStart + (rows.GetInt64(2) - _from) / _speed;
                var remaining = Math.Min(targetMs, seconds * 1000d) - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), token).ConfigureAwait(false);
                if (Stopwatch.GetElapsedTime(started).TotalSeconds >= seconds) return;
                Emit(rows, cycle, emit);
            }
        }
    }
    private SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        command.Parameters.AddWithValue("$s", _session); command.Parameters.AddWithValue("$from", _from); command.Parameters.AddWithValue("$to", _to);
        return command;
    }
    private void Emit(SqliteDataReader row, long cycle, Action<LiveEvent> emit)
    {
        var now = DateTimeOffset.UtcNow;
        var metadata = new Dictionary<string, object?>();
        if (row.GetInt32(0) == 1)
        {
            var profile = JsonSerializer.Deserialize<LiveRoomProfile>(row.GetString(10), Json)!;
            if (profile.LiveStatus is 0 or 2) { Interlocked.Increment(ref _skippedOffline); return; }
            metadata["room_profile"] = profile with { LiveStartedAt = profile.LiveStartedAt + _profileTimeShift };
            emit(new(LiveEventType.Online, "系统", "本地回放", "#FFFFFF", Metadata: metadata)); return;
        }
        if (row.GetInt32(0) == 2)
        {
            if (!row.IsDBNull(11)) metadata["popularity"] = row.GetInt64(11).ToString();
            if (!row.IsDBNull(12)) metadata["watched"] = row.GetInt64(12).ToString();
            emit(new(LiveEventType.Online, "系统", "本地回放", "#FFFFFF", Metadata: metadata));
            var members = row.IsDBNull(13) ? [] : JsonSerializer.Deserialize<LiveRankMember[]>(row.GetString(13), Json) ?? [];
            int? total = row.IsDBNull(14) ? null : checked((int)row.GetInt64(14));
            Volatile.Write(ref _rank, new(members, false, total));
            metadata = new() { ["rank_members"] = members, ["rank_total"] = (long?)total, ["rank_sampled_at"] = now };
            emit(new(LiveEventType.Rank, "系统", "高能榜", "#FFFFFF", Metadata: metadata)); return;
        }
        var fields = JsonSerializer.Deserialize<HistoryEventFields>(row.GetString(9), Json) ?? new();
        var content = row.GetString(6);
        Interlocked.Add(ref _mediaReferences, MediaDetector.Detect(content).Count);
        fields = fields with { UniqueId = fields.UniqueId is null ? null : $"replay:{cycle}:{row.GetInt64(1)}:{fields.UniqueId}", UpstreamTime = null };
        var appearance = row.IsDBNull(8) ? null : JsonSerializer.Deserialize<LiveMessageAppearance>(row.GetString(8), Json);
        var type = (LiveEventType)row.GetInt32(3);
        // The archive intentionally omits arbitrary transport metadata. Restore
        // only explicitly stored fields used by production gift merging/labels.
        // Missing fields stay missing; never infer a price or sender identity.
        if (type == LiveEventType.Gift)
        {
            Add("gift_id", fields.GiftId); Add("gift_name", fields.GiftName);
            Add("num", fields.Quantity); Add("coin_type", fields.CoinType);
            Add("total_coin", fields.TotalCoin); Add("price", fields.UnitPrice);
            Add("transaction_id", fields.TransactionId); Add("gift_icon", fields.GiftIconUrl);
        }
        else if (type == LiveEventType.SuperChat && fields.AmountMilliYuan is { } amount)
            metadata["price"] = amount / 1000m;
        emit(new(type, row.GetString(5), content, row.GetString(7), row.GetInt64(4), now.ToLocalTime(), Metadata: metadata, Appearance: appearance) { HistoryFields = fields });
        Interlocked.Increment(ref _events);
        void Add(string key, object? value) { if (value is not null) metadata[key] = value; }
    }
    private sealed class CachedImages(ArchivedReplay owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (request.RequestUri is not { } uri) throw new InvalidOperationException("回放图片地址缺失");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
            var path = Path.Combine(owner._images, hash + ".img");
            if (!File.Exists(path))
            {
                Interlocked.Increment(ref owner._missingImages);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 2 * 1024 * 1024) { file.Dispose(); throw new InvalidDataException("回放图片超限"); }
            Interlocked.Increment(ref owner._loadedImages);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(file) };
            response.Content.Headers.ContentLength = file.Length; return Task.FromResult(response);
        }
    }
}

// Media reference cards still follow their normal failure UI, without making
// unrecorded external requests. The numeric counter makes this limitation clear.
internal sealed class OfflineReplayMedia : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
