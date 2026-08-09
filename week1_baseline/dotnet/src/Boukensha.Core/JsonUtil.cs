using System.Text.Json;
using System.Text.Json.Nodes;

namespace Boukensha.Core;

public static class JsonUtil
{
    public static object? ToObject(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value => ToObject(value.GetValue<JsonElement>()),
        JsonArray array => array.Select(ToObject).ToList(),
        JsonObject obj => obj.ToDictionary(kv => kv.Key, kv => ToObject(kv.Value)),
        _ => null,
    };

    public static object? ToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => element.EnumerateArray().Select(ToObject).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToObject(p.Value)),
        _ => null,
    };

    public static JsonNode? ToJsonNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        IReadOnlyDictionary<string, object?> dict =>
            new JsonObject(dict.Select(kv => KeyValuePair.Create(kv.Key, ToJsonNode(kv.Value))!)),
        System.Collections.IEnumerable list and not string =>
            new JsonArray(list.Cast<object?>().Select(ToJsonNode).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };
}
