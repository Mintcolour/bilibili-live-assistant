namespace BilibiliLiveAssistant.Core;

public sealed record HistoryAnalysisSettings
{
    public int CountsSeconds { get; init; } = 60;
    public int EnergySeconds { get; init; } = 60;
    public int GuardSeconds { get; init; } = 300;
    public int EnergyLimit { get; init; } = 50;
    public int GuardLimit { get; init; } = 200;
    public HistoryAnalysisSettings Normalize() => this with
    {
        CountsSeconds = Math.Clamp(CountsSeconds, 30, 3600), EnergySeconds = Math.Clamp(EnergySeconds, 30, 3600),
        GuardSeconds = Math.Clamp(GuardSeconds, 60, 7200), EnergyLimit = Math.Clamp(EnergyLimit, 1, 200), GuardLimit = Math.Clamp(GuardLimit, 1, 300),
    };
}

public enum HistorySampleKind { Counts, Energy, Guards }
// Null means unknown; a partial list never implies that an absent person has left.
public sealed record HistoryAnalysisSample(HistorySampleKind Kind, DateTimeOffset At, int IntervalSeconds,
    long? Fans, long? Guards, long? Energy, long? Popularity, long? Watched,
    IReadOnlyList<LiveRankMember> Members, int Limit, long? Total, string Status = "ok")
{
    public int Collected { get; init; }
}
public sealed record HistoryTrendPoint(long Id, string SessionId, HistoryAnalysisSample Sample)
{
    public override string ToString() => $"{Sample.At.ToLocalTime():MM-dd HH:mm:ss} · {Sample.Kind switch { HistorySampleKind.Counts => "人数", HistorySampleKind.Energy => "高能榜", _ => "大航海总榜" }} · {Sample.Status}";
}
public sealed record HistoryMemberState(long Uid, string Name, LiveMessageAppearance Appearance,
    int? Rank = null, long? Contribution = null, int? AccompanyDays = null);
public sealed record HistoryMemberChange(long Id, string SessionId, DateTimeOffset At, string Source,
    string Kind, HistoryMemberState? Before, HistoryMemberState After);
public sealed record HistoryChangeCursor(DateTimeOffset At, long Id);
