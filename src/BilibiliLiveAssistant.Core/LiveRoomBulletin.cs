namespace BilibiliLiveAssistant.Core;

public sealed record LiveRankMember(int Rank, long Uid, string Username, LiveMessageAppearance Appearance,
    bool HasMedalData = true, bool HasGuardData = true, long? Contribution = null, int? AccompanyDays = null);

public enum DashboardRankKind { Energy, GuardTotal, GuardMonth, GuardWeek }
public sealed record DashboardRankPage(IReadOnlyList<LiveRankMember> Members, bool HasMore, int? Total)
{
    public DateTimeOffset SampledAt { get; init; } = DateTimeOffset.UtcNow;
}
public sealed record LiveRoomProfile(long Uid, int RoomId, string Name, string AvatarUrl, string Title,
    string MedalName, long? FansClub, long? Guards, long? Likes, long? EnergyMembers)
{
    public int? LiveStatus { get; init; }
    public DateTimeOffset? LiveStartedAt { get; init; }
}
public sealed record PopularitySample(DateTimeOffset Time, long Value);
public sealed record AudienceCountSample(DateTimeOffset Time, long? Value);

public enum LiveAudienceMetricKind { Popularity, Watched, EnergyMembers }
public sealed record LiveAudienceMetric(LiveAudienceMetricKind Kind, string Value);

// A bounded part of one room bulletin. Contains data only, never decoded pictures.
public sealed record LiveRoomBulletin(IReadOnlyList<LiveAudienceMetric> Metrics,
    IReadOnlyList<LiveRankMember> Members, bool ShowRankHeader);
