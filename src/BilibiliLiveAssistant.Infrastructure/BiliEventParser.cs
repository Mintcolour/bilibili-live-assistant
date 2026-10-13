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
            case "DANMU_MSG":
                var info = root.GetProperty("info");
                if (info.ValueKind != JsonValueKind.Array || info.GetArrayLength() < 3) break;
                var user = info[2];
                if (user.ValueKind != JsonValueKind.Array || user.GetArrayLength() < 2) break;
                var color = info[0].ValueKind == JsonValueKind.Array && info[0].GetArrayLength() > 3
                    ? Number(info[0][3]) : 0xFFFFFF;
                emit(new LiveEvent(LiveEventType.Danmaku, Name(Text(user[1])), Text(info[1]), $"#{color & 0xFFFFFF:X6}", Number(user[0]),
                    Appearance: BiliMessageAppearanceParser.Danmaku(info)));
                break;
            case "SEND_GIFT":
                var gift = root.GetProperty("data");
                EmitGift(emit, Text(gift, "uname"), Number(gift, "uid"), Text(gift, "giftName"), Number(gift, "num"),
                    Text(gift, "action"), Text(gift, "coin_type"), Number(gift, "total_coin"), Number(gift, "giftId"), Number(gift, "price"), Text(gift, "tid"), BiliMessageAppearanceParser.Common(gift));
                break;
            case "SEND_GIFT_V2":
                var broadcast = Decode<SendGiftBroadcast>(root.GetProperty("data"));
                foreach (var item in broadcast.Gifts)
                    EmitGift(emit, broadcast.Username, broadcast.Uid, item.Name, item.Count, item.Action,
                        item.CoinType, item.TotalCoin, item.Id, item.Price, item.TransactionId);
                break;
            case "SUPER_CHAT_MESSAGE":
                var sc = root.GetProperty("data");
                emit(new LiveEvent(LiveEventType.SuperChat, Name(Text(sc.GetProperty("user_info"), "uname")),
                    $"¥{Text(sc, "price")} {Text(sc, "message")}", "#FFD700", Number(sc, "uid"),
                    Metadata: new Dictionary<string, object?> { ["id"] = Number(sc, "id"), ["price"] = Text(sc, "price") }, Appearance: BiliMessageAppearanceParser.Common(sc)));
                break;
            case "INTERACT_WORD_V2":
                var interaction = Decode<InteractWordV2>(root.GetProperty("data"));
                EmitInteraction(emit, interaction.Uname, interaction.Uid, interaction.MsgType, BiliMessageAppearanceParser.Interaction(interaction));
                break;
            case "INTERACT_WORD":
                var legacy = root.GetProperty("data");
                EmitInteraction(emit, Text(legacy, "uname"), Number(legacy, "uid"), (int)Number(legacy, "msg_type"), BiliMessageAppearanceParser.Common(legacy));
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
                    $"开通了{guardName}！", "#FF69B4", Number(sender, "uid"), Appearance: BiliMessageAppearanceParser.Common(toast)));
                break;
            case "WATCHED_CHANGE":
                var watched = root.GetProperty("data");
                var watchedText = Text(watched, "text_large");
                if (watchedText.Length == 0) watchedText = Text(watched, "num");
                emit(new LiveEvent(LiveEventType.Online, "系统", $"看过: {watchedText}", "#2196F3",
                    Metadata: new Dictionary<string, object?> { ["watched"] = watchedText }));
                break;
        }
    }

    private static void EmitGift(Action<LiveEvent> emit, string username, long uid, string name, long count,
        string action, string coinType, long totalCoin, long giftId, long price, string transactionId, LiveMessageAppearance? appearance = null)
    {
        if (name.Length == 0 || count <= 0) return;
        var amount = coinType == "gold" && totalCoin > 0
            ? $" · ¥{(totalCoin / 1000m).ToString("0.###", CultureInfo.InvariantCulture)}" : string.Empty;
        emit(new LiveEvent(LiveEventType.Gift, Name(username), $"{(action.Length == 0 ? "赠送" : action)} {name}×{count}{amount}", "#FF69B4", uid,
            Metadata: new Dictionary<string, object?>
            {
                ["gift_id"] = giftId, ["num"] = count, ["coin_type"] = coinType,
                ["total_coin"] = totalCoin, ["price"] = price, ["transaction_id"] = transactionId,
                ["gift_name"] = name,
            }, Appearance: appearance));
    }

    private static void EmitInteraction(Action<LiveEvent> emit, string username, long uid, int type, LiveMessageAppearance? appearance = null)
    {
        var action = type switch { 1 => "进入了直播间", 2 => "关注了主播", 3 => "分享了直播间", 4 => "特别关注了主播", 5 => "互粉了", 6 => "为主播点赞了", _ => "互动了" };
        emit(new LiveEvent(LiveEventType.Enter, Name(username), action, "#4CAF50", uid, Appearance: appearance));
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
}

// Field numbers match blivedm's SEND_GIFT_V2 model; unused fields are skipped.
[ProtoContract]
internal sealed class SendGiftBroadcast
{
    [ProtoMember(1)] public long Uid { get; set; }
    [ProtoMember(2)] public string Username { get; set; } = string.Empty;
    [ProtoMember(10)] public List<SendGiftItem> Gifts { get; } = new();
}

[ProtoContract]
internal sealed class SendGiftItem
{
    [ProtoMember(1)] public long Id { get; set; }
    [ProtoMember(2)] public string Name { get; set; } = string.Empty;
    [ProtoMember(3)] public long Count { get; set; }
    [ProtoMember(5)] public long Price { get; set; }
    [ProtoMember(7)] public long TotalCoin { get; set; }
    [ProtoMember(8)] public string CoinType { get; set; } = string.Empty;
    [ProtoMember(9)] public string TransactionId { get; set; } = string.Empty;
    [ProtoMember(18)] public string Action { get; set; } = string.Empty;
}
