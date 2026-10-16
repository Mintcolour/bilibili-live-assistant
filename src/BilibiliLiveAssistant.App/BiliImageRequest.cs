using System.Text.RegularExpressions;

namespace BilibiliLiveAssistant.App;

internal enum BiliImageRole { Content, Avatar, Emote, Decoration, MediaCover }

internal readonly record struct BiliImageRequest(Uri Original, Uri Address, int Limit, BiliImageRole Role)
{
    private static readonly Regex AvatarPath = new(@"^/bfs/face/[a-fA-F0-9]{40}\.(?:jpg|jpeg|png|webp)$", RegexOptions.CultureInvariant);
    internal static BiliImageRequest? Create(string url, int limit, BiliImageRole role)
    {
        if (BiliImageCache.NormalizeUrl(url) is not { } original) return null;
        limit = BiliImageCache.SizeTier(limit);
        var address = original;
        // Only verified avatar paths. Queries, existing transforms, animated
        // resources and unknown paths retain their exact original request.
        if (role == BiliImageRole.Avatar && original.Query.Length == 0
            && original.Host is "i0.hdslb.com" or "i1.hdslb.com" or "i2.hdslb.com"
            && AvatarPath.IsMatch(original.AbsolutePath))
            address = new(original.AbsoluteUri + $"@{limit}w_{limit}h_0e.png");
        return new(original, address, limit, role);
    }
}
