using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using BilibiliLiveAssistant.Core;

namespace BilibiliLiveAssistant.Infrastructure;

// Archive links and appearance metadata, never image bodies. Omitted appearance
// defaults are restored by the existing model initializers on old/new reads.
internal static class HistoryJson
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(LiveMessageAppearance)) return;
            foreach (var property in info.Properties)
            {
                var name = property.Name;
                property.ShouldSerialize = (_, value) => value is not null && !IsAppearanceDefault(name, value);
            }
        });
        return new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = resolver,
        };
    }
    // Never omit nullable numeric zero (free/zero is different from unknown),
    // required constructor fields, or empty collections outside appearance.
    private static bool IsAppearanceDefault(string name, object? value) => name switch
    {
        "avatarUrl" or "avatarFrameUrl" or "usernameColor" or "medalName" or
        "interactionLabel" or "interactionIconUrl" or "interactionHint" => value is string { Length: 0 },
        "medalColor" => value is "#527EBE",
        "medalLevel" or "guardLevel" or "wealthLevel" => value is 0,
        "isAdmin" => value is false,
        "emotes" => value is IReadOnlyList<LiveEmote> { Count: 0 },
        _ => false,
    };

    // Maintenance preserves unknown fields and all array elements. Only null
    // object properties and precisely identified appearance defaults disappear.
    internal static string CompactStored(string data, bool appearanceRoot = false)
    {
        var node = JsonNode.Parse(data);
        if (node is null) return data;
        var changed = false;
        Visit(node, appearanceRoot);
        return changed ? node.ToJsonString(Options) : data;

        void Visit(JsonNode current, bool appearance)
        {
            if (current is JsonObject obj)
            {
                foreach (var (name, value) in obj.ToArray())
                {
                    if (value is null || appearance && IsStoredDefault(name, value))
                    { obj.Remove(name); changed = true; }
                    else Visit(value, name == "appearance");
                }
            }
            else if (current is JsonArray array)
                foreach (var child in array) if (child is not null) Visit(child, false);
        }
    }
    private static bool IsStoredDefault(string name, JsonNode value)
    {
        if (value is JsonArray { Count: 0 }) return name == "emotes";
        if (value is not JsonValue scalar) return false;
        if (scalar.TryGetValue<string>(out var text)) return IsAppearanceDefault(name, text);
        if (scalar.TryGetValue<int>(out var number)) return IsAppearanceDefault(name, number);
        if (scalar.TryGetValue<bool>(out var flag)) return IsAppearanceDefault(name, flag);
        return false;
    }
}
