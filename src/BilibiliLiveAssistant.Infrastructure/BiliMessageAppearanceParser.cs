using System.Text.Json;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

internal static class BiliMessageAppearanceParser
{
    internal static JsonElement At(JsonElement value, int index) => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > index ? value[index] : default;
    internal static JsonElement Get(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var found) ? found : default;
    internal static string Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";
    internal static int Int(JsonElement value) => int.TryParse(Text(value), out var n) ? n : 0;
    private static string Color(JsonElement value, string fallback = "#527EBE") => Int(value) is > 0 and var c ? $"#{c & 0xFFFFFF:X6}" : fallback;

    public static LiveMessageAppearance Danmaku(JsonElement info)
    {
        var options = At(info, 0);
        var user = At(info, 2);
        var medal = At(info, 3);
        var mode = At(options, 15);
        return ReadObjectOrJson(mode, node =>
        {
            var profile = Get(Get(node, "user"), "base");
            var face = Text(Get(profile, "face"));
            if (face.Length == 0) face = Text(Get(Get(Get(node, "uinfo"), "base"), "face"));
            if (face.Length == 0) face = Text(Get(Get(Get(node, "sender_uinfo"), "base"), "face"));
            if (face.Length == 0) face = Text(Get(Get(node, "user_info"), "face"));
            if (face.Length == 0) face = First(Text(Get(node, "face")), Text(Get(node, "uface")));
            var full = ReadObjectOrJson(At(options, 13), e => Emote(e, ""));
            var emotes = ReadObjectOrJson(Get(node, "extra"), ReadInlineEmotes) ?? Array.Empty<LiveEmote>();
            return new LiveMessageAppearance
            {
                AvatarUrl = Limit(face, 2048), UsernameColor = Color(At(user, 7), ""),
                MedalName = Text(At(medal, 1)), MedalLevel = Int(At(medal, 0)), MedalColor = Color(At(medal, 4)),
                GuardLevel = Int(At(info, 7)), IsAdmin = Int(At(user, 2)) == 1,
                FullEmote = Int(At(options, 12)) == 1 ? full : null, Emotes = emotes,
            };
        }) ?? new LiveMessageAppearance
        {
            MedalName = Text(At(medal, 1)), MedalLevel = Int(At(medal, 0)), MedalColor = Color(At(medal, 4)),
            GuardLevel = Int(At(info, 7)), IsAdmin = Int(At(user, 2)) == 1,
            FullEmote = Int(At(options, 12)) == 1 ? ReadObjectOrJson(At(options, 13), e => Emote(e, "")) : null,
        };
    }

    public static LiveMessageAppearance Common(JsonElement data)
    {
        var uinfo = Get(data, "uinfo");
        var sender = Get(data, "sender_uinfo");
        if (uinfo.ValueKind != JsonValueKind.Object) uinfo = sender;
        if (uinfo.ValueKind != JsonValueKind.Object) uinfo = Get(data, "user");
        var user = Get(data, "user_info");
        if (user.ValueKind != JsonValueKind.Object) user = Get(uinfo, "base");
        var medal = Get(data, "medal_info");
        if (medal.ValueKind != JsonValueKind.Object) medal = Get(data, "fans_medal");
        if (medal.ValueKind != JsonValueKind.Object) medal = Get(uinfo, "medal");
        if (medal.ValueKind != JsonValueKind.Object) medal = Get(sender, "medal");
        var face = Text(Get(user, "face"));
        if (face.Length == 0) face = Text(Get(data, "face"));
        if (face.Length == 0) face = Text(Get(Get(uinfo, "base"), "face"));
        if (face.Length == 0) face = Text(Get(Get(sender, "base"), "face"));
        if (face.Length == 0) face = Text(Get(data, "uface"));
        var level = Int(Get(user, "guard_level"));
        if (level == 0) level = Int(Get(Get(uinfo, "guard"), "level"));
        if (level == 0) level = Int(Get(Get(sender, "guard"), "level"));
        if (level == 0) level = Int(Get(Get(data, "guard_info"), "guard_level"));
        if (level == 0) level = Int(Get(data, "privilege_type"));
        if (level == 0) level = Int(Get(medal, "guard_level"));
        var relation = Get(data, "relation_tail");
        return new LiveMessageAppearance
        {
            AvatarUrl = Limit(face, 2048), AvatarFrameUrl = Limit(First(Text(Get(Get(uinfo, "uhead_frame"), "frame_img")), Text(Get(Get(sender, "uhead_frame"), "frame_img"))), 2048),
            UsernameColor = DisplayColor(First(Text(Get(data, "uname_color")), Text(Get(user, "name_color_str"))), Color(Get(user, "name_color"), "")),
            MedalName = Limit(First(Text(Get(medal, "medal_name")), Text(Get(medal, "name"))), 64),
            MedalLevel = Math.Clamp(Math.Max(Int(Get(medal, "medal_level")), Int(Get(medal, "level"))), 0, 999),
            MedalColor = Color(Get(medal, "medal_color"), Color(Get(medal, "color"))), GuardLevel = level is >= 1 and <= 3 ? level : 0,
            WealthLevel = Math.Clamp(Math.Max(Int(Get(Get(uinfo, "wealth"), "level")), Int(Get(Get(data, "wealthy_info"), "level"))), 0, 999),
            IsAdmin = Int(Get(user, "manager")) == 1,
            InteractionLabel = Limit(First(Text(Get(data, "tail_text")), Text(Get(data, "spread_desc"))), 32),
            InteractionIconUrl = Limit(Text(Get(relation, "tail_icon")), 2048),
            InteractionHint = Limit(Text(Get(relation, "tail_guide_text")), 256),
        };
    }

    public static LiveMessageAppearance Interaction(InteractWordV2 value)
    {
        var user = value.User;
        var level = user?.Guard?.Level ?? 0;
        if (level == 0) level = user?.Medal?.GuardLevel ?? 0;
        if (level == 0) level = value.GuardLevel;
        if (level == 0) level = value.Medal?.GuardLevel ?? 0;
        var color = user?.Medal?.Color ?? value.Medal?.Color ?? 0;
        return new LiveMessageAppearance
        {
            AvatarUrl = Limit(user?.Base?.Face ?? "", 2048), AvatarFrameUrl = Limit(user?.Frame?.Url ?? "", 2048),
            UsernameColor = DisplayColor(First(value.UsernameColor, user?.Base?.NameColorText ?? ""),
                user?.Base?.NameColor is > 0 and var nameColor ? $"#{nameColor & 0xFFFFFF:X6}" : ""),
            MedalName = Limit(user?.Medal?.Name ?? value.Medal?.Name ?? "", 64),
            MedalLevel = Math.Clamp(user?.Medal?.Level ?? value.Medal?.Level ?? 0, 0, 999),
            MedalColor = color > 0 ? $"#{color & 0xFFFFFF:X6}" : "#527EBE", GuardLevel = level is >= 1 and <= 3 ? level : 0,
            WealthLevel = Math.Clamp(user?.Wealth?.Level ?? 0, 0, 999),
            InteractionLabel = Limit(First(value.TailText, value.SpreadDescription), 32),
            InteractionIconUrl = Limit(value.Relation?.IconUrl ?? "", 2048), InteractionHint = Limit(value.Relation?.Hint ?? "", 256),
        };
    }

    public static LiveRankMember RankMember(JsonElement data, int rank)
    {
        var common = Common(data);
        var uinfo = Get(data, "uinfo");
        var medalSources = new[] { Get(data, "medal_info"), Get(data, "medalInfo"), Get(data, "fans_medal"), Get(data, "medal"), Get(uinfo, "medal") };
        var medal = medalSources.FirstOrDefault(source => source.ValueKind == JsonValueKind.Object);
        var guard = Get(data, "guard_level");
        if (guard.ValueKind == JsonValueKind.Undefined) guard = Get(data, "guardLevel");
        if (guard.ValueKind == JsonValueKind.Undefined) guard = Get(data, "privilege_type");
        if (guard.ValueKind == JsonValueKind.Undefined) guard = Get(medal, "guard_level");
        if (guard.ValueKind == JsonValueKind.Undefined) guard = Get(medal, "guardLevel");
        if (guard.ValueKind == JsonValueKind.Undefined) guard = Get(Get(uinfo, "guard"), "level");
        if (guard.ValueKind == JsonValueKind.Undefined) guard = Get(Get(data, "user_info"), "guard_level");
        if (guard.ValueKind == JsonValueKind.Undefined) guard = Get(Get(data, "guard_info"), "guard_level");
        var name = Text(Get(data, "name"));
        if (name.Length == 0) name = Text(Get(data, "uname"));
        if (name.Length == 0) name = Text(Get(Get(uinfo, "base"), "name"));
        var medalName = First(Text(Get(medal, "medal_name")), First(Text(Get(medal, "medalName")), Text(Get(medal, "name"))));
        var medalLevel = Math.Max(Int(Get(medal, "medal_level")), Math.Max(Int(Get(medal, "medalLevel")), Int(Get(medal, "level"))));
        var uidText = Text(Get(data, "uid"));
        if (uidText.Length == 0) uidText = Text(Get(uinfo, "uid"));
        var uid = long.TryParse(uidText, out var parsedUid) ? Math.Max(0, parsedUid) : 0;
        var appearance = common with
        {
            AvatarUrl = Limit(First(common.AvatarUrl, Text(Get(data, "uface"))), 2048),
            MedalName = Limit(First(medalName, common.MedalName), 64),
            MedalLevel = Math.Clamp(Math.Max(common.MedalLevel, medalLevel), 0, 999),
            MedalColor = Color(Get(medal, "medal_color"), Color(Get(medal, "medalColor"), Color(Get(medal, "color"), common.MedalColor))),
            GuardLevel = guard.ValueKind != JsonValueKind.Undefined ? Int(guard) is >= 1 and <= 3 ? Int(guard) : 0 : common.GuardLevel,
        };
        return new(rank, uid, Limit(name.Length > 0 ? name : "匿名观众", 64), appearance,
            medalSources.Any(source => source.ValueKind != JsonValueKind.Undefined), guard.ValueKind != JsonValueKind.Undefined,
            Long(Get(data, "score")), Long(Get(data, "accompany")) is >= 0 and <= int.MaxValue and var days ? (int)days : null);
    }

    internal static long? Long(JsonElement value) => long.TryParse(Text(value), out var n) && n >= 0 ? n : null;

    public static LiveMessageAppearance Entry(JsonElement data)
    {
        var appearance = Common(data);
        return appearance with { EntryEffect = new LiveEntryEffect(
            Limit(First(Text(Get(data, "web_basemap_url")), Text(Get(data, "basemap_url"))), 2048),
            DisplayColor(Text(Get(data, "copy_color")), "#FFFFFF"), DisplayColor(Text(Get(data, "highlight_color")), "#FFFFFF"),
            Limit(Text(Get(Get(data, "wealth_style_info"), "url")), 2048)) };
    }

    private static string First(string first, string second) => string.IsNullOrWhiteSpace(first) ? second : first;
    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    internal static string DisplayColor(string value, string fallback)
    {
        // Some user_dagw colors append alpha after RGB. Opacity is controlled by display settings.
        if (value.Length == 9 && value[0] == '#') value = value[..7];
        return OverlayRegionSettings.IsColor(value) ? value : fallback;
    }
    private static LiveEmote[] ReadInlineEmotes(JsonElement extra)
    {
        var emots = Get(extra, "emots");
        if (emots.ValueKind != JsonValueKind.Object) return Array.Empty<LiveEmote>();
        var result = new List<LiveEmote>(16);
        foreach (var entry in emots.EnumerateObject().Take(32))
            if (entry.Name.Length is > 0 and <= 128 && Emote(entry.Value, entry.Name) is { } emote) result.Add(emote);
        return result.ToArray();
    }
    private static LiveEmote? Emote(JsonElement data, string token)
    {
        var url = Text(Get(data, "url"));
        if (url.Length is 0 or > 2048) return null;
        var width = Int(Get(data, "width")); var height = Int(Get(data, "height"));
        return new(token, url, Math.Clamp(width > 0 ? width : 24, 1, 4096), Math.Clamp(height > 0 ? height : 24, 1, 4096), Int(Get(data, "is_dynamic")) == 1);
    }
    private static T? ReadObjectOrJson<T>(JsonElement value, Func<JsonElement, T?> parse) where T : class
    {
        if (value.ValueKind == JsonValueKind.Object) return parse(value);
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        if (text is null || text.Length > 65536) return null;
        try { using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 24 }); return parse(doc.RootElement); }
        catch (JsonException) { return null; }
    }
}
