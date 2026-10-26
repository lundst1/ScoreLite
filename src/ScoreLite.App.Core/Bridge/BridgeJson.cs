using System.Text.Json;
using System.Text.Json.Serialization;
using ScoreLite.Core;

namespace ScoreLite.App.Bridge;

/// <summary>
/// IF-5 / S4. All data till JavaScript är JSON och byggs aldrig ihop som kodsträngar.
/// JS -> C#-meddelanden tolkas tolerant: fel format ger <c>null</c>, aldrig undantag (R1).
/// </summary>
public static class BridgeJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serialiserar ett C# -> JS-meddelande. Körtidstypen används så att <c>t</c> och <c>v</c> kommer med.</summary>
    public static string Serialize(BridgeMessage message) => JsonSerializer.Serialize(message, message.GetType(), Options);

    /// <summary>
    /// JavaScript-stränglitteral av JSON-texten, för <c>EvaluateJavaScript("window.bridge.receive(" + literal + ")")</c>.
    /// JSON-kodningen av en sträng är en giltig JS-stränglitteral, och tecken som &lt; &gt; &amp; escapas av serialiseraren.
    /// </summary>
    public static string ToJsStringLiteral(string json) => JsonSerializer.Serialize(json);

    public static BridgeMessage? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("t", out var tEl) || tEl.ValueKind != JsonValueKind.String) return null;
            string t = tEl.GetString()!;
            return t switch
            {
                "ready" => new ReadyMessage(Str(root, "verovioVersion")),
                "layoutReady" => ParseLayoutReady(root),
                "tap" => new TapMessage(
                    root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null,
                    Int(root, "measure"), Dbl(root, "x")),
                "followChanged" => new FollowChangedMessage(Bool(root, "enabled")),
                "pong" => new PongMessage(Int(root, "id")),
                "renderError" => new RenderErrorMessage(Str(root, "code"), Str(root, "message")),
                _ => new UnknownMessage(t),
            };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static LayoutReadyMessage ParseLayoutReady(JsonElement root)
    {
        var systems = new List<SystemGeometry>();
        if (root.TryGetProperty("systems", out var sEl) && sEl.ValueKind == JsonValueKind.Array)
            foreach (var s in sEl.EnumerateArray())
                systems.Add(new SystemGeometry(Int(s, "sys"), Dbl(s, "y"), Dbl(s, "h")));

        var items = new List<LayoutItem>();
        if (root.TryGetProperty("items", out var iEl) && iEl.ValueKind == JsonValueKind.Array)
            foreach (var it in iEl.EnumerateArray())
            {
                // [id, measure, x, sys]
                if (it.ValueKind != JsonValueKind.Array || it.GetArrayLength() < 4) continue;
                var a = it.EnumerateArray().ToArray();
                if (a[0].ValueKind != JsonValueKind.String) continue;
                items.Add(new LayoutItem(a[0].GetString()!, a[1].GetInt32(), a[2].GetDouble(), a[3].GetInt32()));
            }
        return new LayoutReadyMessage(Int(root, "requestId"), Int(root, "layoutGen"), systems, items);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
    private static double Dbl(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
