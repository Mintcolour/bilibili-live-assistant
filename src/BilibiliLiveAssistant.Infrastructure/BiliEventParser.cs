using System.Globalization;
using System.Text.Json;
using BilibiliLiveAssistant.Core;
using ProtoBuf;

namespace BilibiliLiveAssistant.Infrastructure;

internal static class BiliEventParser
{
    public static void Parse(JsonElement root, Action<LiveEvent> emit)
    {
        var command = Text(root, "cmd");
        var suffix = command.IndexOf(':');
        if (suffix >= 0) command = command[..suffix];
        switch (command)
        {
            case "PREPARING":
                // Explicit room lifecycle event; socket disconnection alone is not offline.
                emit(new LiveEvent(LiveEventType.Online, "系统", "直播已关闭", "#D6B35A",
                    Metadata: new Dictionary<string, object?> { ["live_status"] = 0, ["room_summary"] = "直播已关闭" }));
                break;
            case "DANMU_MSG":
                var info = root.GetProperty("info");
                if (info.ValueKind != JsonValueKind.Array || info.GetArrayLength() < 3) break;
                var user = info[2];
                if (user.ValueKind != JsonValueKind.Array || user.GetArrayLength() < 2) break;
                var color = info[0].ValueKind == JsonValueKind.Array && info[0].GetArrayLength() > 3
                    ? Number(info[0][3]) : 0xFFFFFF;
                emit(new LiveEvent(LiveEventType.Danmaku, Name(Text(user[1])), Text(info[1]), $"#{color & 0xFFFFFF:X6}", Number(user[0]),
                    Appearance: BiliMessageAppearanceParser.Danmaku(info))
                { HistoryFields = new(UpstreamTime: info[0].ValueKind == JsonValueKind.Array && info[0].GetArrayLength() > 4 ? SourceTime(info[0][4]) : null) });
                break;
            case "SEND_GIFT":
                var gift = root.GetProperty("data");
                EmitGift(emit, Text(gift, "uname"), Number(gift, "uid"), Text(gift, "giftName"), Number(gift, "num"),
                    Text(gift, "action"), Text(gift, "coin_type"), OptionalNumber(gift, "total_coin"), Number(gift, "giftId"), OptionalNumber(gift, "price"), Text(gift, "tid"), BiliMessageAppearanceParser.Common(gift), SourceTime(gift, "timestamp"),
                    Text(gift, "gift_icon") is { Length: > 0 } icon ? icon : BiliMessageAppearanceParser.Text(BiliMessageAppearanceParser.Get(BiliMessageAppearanceParser.Get(gift, "gift_info"), "img_basic")));
                break;
            case "SEND_GIFT_V2":
                var broadcast = Decode<SendGiftBroadcast>(root.GetProperty("data"));
                var giftAppearance = new LiveMessageAppearance
                {
                    AvatarUrl = broadcast.Face.Length <= 2048 ? broadcast.Face : "",
                    GuardLevel = broadcast.GuardLevel is >= 1 and <= 3 ? broadcast.GuardLevel : 0,
                    MedalName = broadcast.Medal?.Name is { } medalName ? medalName[..Math.Min(medalName.Length, 64)] : "",
                    MedalLevel = Math.Clamp(broadcast.Medal?.Level ?? 0, 0, 999),
                };
                foreach (var item in broadcast.Gifts)
                    EmitGift(emit, broadcast.Username, broadcast.Uid, item.Name, item.Count, item.Action,
                        item.CoinType, item.TotalCoin, item.Id, item.Price, item.TransactionId, giftAppearance,
                        SourceTime(item.Timestamp), item.Material?.IconUrl is { Length: <= 2048 } giftIcon ? giftIcon : "");
                break;
            case "SUPER_CHAT_MESSAGE":
                var sc = root.GetProperty("data");
                emit(new LiveEvent(LiveEventType.SuperChat, Name(Text(sc.GetProperty("user_info"), "uname")),
                    $"¥{Text(sc, "price")} {Text(sc, "message")}", "#FFD700", Number(sc, "uid"),
                    Metadata: new Dictionary<string, object?> { ["id"] = Number(sc, "id"), ["price"] = Text(sc, "price") }, Appearance: BiliMessageAppearanceParser.Common(sc))
                { HistoryFields = new(UpstreamTime: SourceTime(sc, "start_time"), UniqueId: Number(sc, "id") > 0 ? "sc:" + Number(sc, "id").ToString(CultureInfo.InvariantCulture) : null,
                    AmountMilliYuan: MilliYuan(sc, "price"), RawBody: Text(sc, "message")) });
                break;
            case "INTERACT_WORD_V2":
                var interaction = Decode<InteractWordV2>(root.GetProperty("data"));
                EmitInteraction(emit, interaction.Uname, interaction.Uid, interaction.MsgType, BiliMessageAppearanceParser.Interaction(interaction), SourceTime(interaction.Timestamp));
                break;
            case "INTERACT_WORD":
                var legacy = root.GetProperty("data");
                EmitInteraction(emit, Text(legacy, "uname"), Number(legacy, "uid"), (int)Number(legacy, "msg_type"), BiliMessageAppearanceParser.Common(legacy), SourceTime(legacy, "timestamp"));
                break;
            case "LIKE_INFO_V3_CLICK":
                if (!root.TryGetProperty("data", out var like) || like.ValueKind != JsonValueKind.Object) break;
                var likeUser = BiliMessageAppearanceParser.Get(like, "uinfo");
                var likeName = Text(like, "uname");
                if (likeName.Length == 0) likeName = Text(BiliMessageAppearanceParser.Get(likeUser, "base"), "name");
                var likeUid = Number(like, "uid");
                if (likeUid <= 0) likeUid = Number(likeUser, "uid");
                EmitInteraction(emit, likeName, Math.Max(0, likeUid), 6, BiliMessageAppearanceParser.Common(like), SourceTime(like, "timestamp"));
                break;
            case "LIKE_INFO_V3_UPDATE":
                // A room-wide total is not a named user's click notification.
                break;
            case "ENTRY_EFFECT":
            case "ENTRY_EFFECT_MUST_RECEIVE":
                EmitEntry(emit, root.GetProperty("data"));
                break;
            case "USER_TOAST_MSG_V2":
            case "USER_TOAST_V2":
                var toast = root.GetProperty("data");
                if (toast.TryGetProperty("option", out var option) && Number(option, "source") == 2) break;
                var sender = toast.GetProperty("sender_uinfo");
                var guard = toast.GetProperty("guard_info");
                var level = Number(guard, "guard_level");
                var guardName = level switch { 1 => "总督", 2 => "提督", 3 => "舰长", _ => "大航海" };
                emit(new LiveEvent(LiveEventType.Guard, Name(Text(sender.GetProperty("base"), "name")),
                    $"开通了{guardName}！", "#FF69B4", Number(sender, "uid"), Appearance: BiliMessageAppearanceParser.Common(toast))
                { HistoryFields = new(UpstreamTime: SourceTime(toast, "start_time"), Quantity: OptionalNumber(toast, "num"),
                    UnitPrice: OptionalNumber(toast, "price"), GuardLevel: level is >= 1 and <= 3 ? (int)level : null) });
                break;
            case "WATCHED_CHANGE":
                var watched = root.GetProperty("data");
                var watchedText = Text(watched, "text_large");
                if (watchedText.Length == 0) watchedText = Text(watched, "num");
                emit(new LiveEvent(LiveEventType.Online, "系统", $"看过: {watchedText}", "#2196F3",
                    Metadata: new Dictionary<string, object?> { ["watched"] = watchedText, ["archive_watched"] = OptionalNumber(watched, "num") }));
                break;
        }
    }

    private static void EmitGift(Action<LiveEvent> emit, string username, long uid, string name, long count,
        string action, string coinType, long? totalCoin, long giftId, long? price, string transactionId, LiveMessageAppearance? appearance = null, DateTimeOffset? upstream = null, string iconUrl = "")
    {
        if (name.Length == 0 || count <= 0) return;
        var amount = coinType == "gold" && totalCoin > 0
            ? $" · ¥{(totalCoin.Value / 1000m).ToString("0.###", CultureInfo.InvariantCulture)}" : string.Empty;
        emit(new LiveEvent(LiveEventType.Gift, Name(username), $"{(action.Length == 0 ? "赠送" : action)} {name}×{count}{amount}", "#FF69B4", uid,
            Metadata: new Dictionary<string, object?>
            {
                ["gift_id"] = giftId, ["num"] = count, ["coin_type"] = coinType,
                ["total_coin"] = totalCoin, ["price"] = price, ["transaction_id"] = transactionId,
                ["gift_name"] = name,
                ["gift_icon"] = iconUrl,
            }, Appearance: appearance)
        { HistoryFields = new(UpstreamTime: upstream, GiftId: giftId > 0 ? giftId : null, GiftName: name, Quantity: count,
            CoinType: coinType, UnitPrice: price, TotalCoin: totalCoin, AmountMilliYuan: coinType == "gold" && totalCoin is >= 0 ? totalCoin : null,
            TransactionId: transactionId.Length > 0 ? transactionId : null, GiftIconUrl: iconUrl.Length > 0 ? iconUrl : null) });
    }

    private static void EmitInteraction(Action<LiveEvent> emit, string username, long uid, int type, LiveMessageAppearance? appearance = null, DateTimeOffset? upstream = null)
    {
        var action = type switch { 1 => "进入了直播间", 2 => "关注了主播", 3 => "分享了直播间", 4 => "特别关注了主播", 5 => "互粉了", 6 => "为主播点赞了", _ => "互动了" };
        emit(new LiveEvent(LiveEventType.Enter, Name(username), action, type == 6 ? "#FFAA78" : "#4CAF50", uid, Appearance: appearance)
        { HistoryFields = new(UpstreamTime: upstream, InteractionKind: type) });
    }

    private static void EmitEntry(Action<LiveEvent> emit, JsonElement data)
    {
        var writing = Text(data, "copy_writing_v2");
        if (writing.Length == 0) writing = Text(data, "copy_writing");
        var username = Text(BiliMessageAppearanceParser.Get(BiliMessageAppearanceParser.Get(data, "uinfo"), "base"), "name");
        var nameStart = writing.IndexOf("<%", StringComparison.Ordinal);
        var nameEnd = nameStart >= 0 ? writing.IndexOf("%>", nameStart + 2, StringComparison.Ordinal) : -1;
        if (nameEnd > nameStart + 2 && nameEnd - nameStart <= 130)
        {
            username = writing[(nameStart + 2)..nameEnd];
            writing = writing.Remove(nameStart, nameEnd + 2 - nameStart).Trim();
        }
        else if (username.Length > 0 && writing.StartsWith(username, StringComparison.Ordinal)) writing = writing[username.Length..].Trim();
        if (username.Length == 0) username = Text(data, "uname");
        emit(new LiveEvent(LiveEventType.Enter, Name(username), writing.Length == 0 ? "来了" : writing, "#4CAF50", Number(data, "uid"),
            Appearance: BiliMessageAppearanceParser.Entry(data)));
    }

    private static T Decode<T>(JsonElement data)
    {
        // The protobuf payload is not display text and must not be truncated.
        var encoded = BiliMessageAppearanceParser.Text(BiliMessageAppearanceParser.Get(data, "pb"));
        if (encoded.Length > 2 * 1024 * 1024) throw new InvalidDataException("事件 protobuf 超过允许大小");
        using var stream = new MemoryStream(Convert.FromBase64String(encoded));
        return Serializer.Deserialize<T>(stream);
    }

    private static string Name(string value) => string.IsNullOrWhiteSpace(value) ? "匿名用户" : value[..Math.Min(value.Length, 128)];
    private static string Text(JsonElement value) => value.ValueKind is JsonValueKind.String ? LimitText(value.GetString() ?? string.Empty)
        : value.ValueKind is JsonValueKind.Number ? value.GetRawText() : string.Empty;
    private static string LimitText(string value) => value.Length <= 4096 ? value : value[..4096] + "…";
    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) ? Text(item) : string.Empty;
    private static long Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number
        : long.TryParse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
    private static long Number(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) ? Number(item) : 0;
    private static long? OptionalNumber(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item)
        && long.TryParse(Text(item), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= 0 ? number : null;
    private static DateTimeOffset? SourceTime(JsonElement value, string key) => value.TryGetProperty(key, out var item) ? SourceTime(item) : null;
    private static DateTimeOffset? SourceTime(JsonElement value)
    {
        return SourceTime(Number(value));
    }
    private static DateTimeOffset? SourceTime(long number)
    {
        try { return number is > 100000000000 and < 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds(number)
            : number is > 1000000000 and < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(number) : null; }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    private static long? MilliYuan(JsonElement value, string key) => decimal.TryParse(Text(value, key), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
        && amount >= 0 && amount <= long.MaxValue / 1000m && decimal.Truncate(amount * 1000m) == amount * 1000m ? (long)(amount * 1000m) : null;
}

// Field numbers match blivedm's SEND_GIFT_V2 model; unused fields are skipped.
[ProtoContract]
internal sealed class SendGiftBroadcast
{
    [ProtoMember(1)] public long Uid { get; set; }
    [ProtoMember(2)] public string Username { get; set; } = string.Empty;
    [ProtoMember(3)] public string Face { get; set; } = string.Empty;
    [ProtoMember(5)] public int GuardLevel { get; set; }
    [ProtoMember(8)] public SendGiftMedal? Medal { get; set; }
    [ProtoMember(10)] public List<SendGiftItem> Gifts { get; } = new();
}

[ProtoContract]
internal sealed class SendGiftMedal
{
    [ProtoMember(5)] public int Level { get; set; }
    [ProtoMember(6)] public string Name { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class SendGiftMaterial
{
    [ProtoMember(1)] public string IconUrl { get; set; } = string.Empty;
}

[ProtoContract]
internal sealed class SendGiftItem
{
    [ProtoMember(1)] public long Id { get; set; }
    [ProtoMember(2)] public string Name { get; set; } = string.Empty;
    [ProtoMember(3)] public long Count { get; set; }
    [ProtoMember(5)] public long? Price { get; set; }
    [ProtoMember(7)] public long? TotalCoin { get; set; }
    [ProtoMember(8)] public string CoinType { get; set; } = string.Empty;
    [ProtoMember(9)] public string TransactionId { get; set; } = string.Empty;
    [ProtoMember(10)] public long Timestamp { get; set; }
    [ProtoMember(18)] public string Action { get; set; } = string.Empty;
    [ProtoMember(35)] public SendGiftMaterial? Material { get; set; }
}
