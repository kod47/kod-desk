using System.Text.Json;
using System.Text.Json.Serialization;

namespace KodDesk.Protocol;

/// <summary>
/// Mesaje text (JSON) schimbate cu serverul relay.
///
/// Endpoint-uri:
///   /ws/host?k=CHEIE              – canalul de control al host-ului (înregistrare, notificări de conexiune)
///   /ws/connect/{id}?k=CHEIE      – viewer-ul cere conectarea la host-ul cu ID-ul dat
///   /ws/session/{token}?k=CHEIE   – host-ul deschide canalul de date pentru o sesiune
/// După ce relay-ul trimite "connected" viewer-ului, tot traficul binar este retransmis nemodificat.
/// </summary>
public sealed class RelayMessage
{
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("secret")] public string? Secret { get; set; }
    [JsonPropertyName("session")] public string? Session { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }

    public const string Register = "register";
    public const string Registered = "registered";
    public const string Incoming = "incoming";
    public const string Connected = "connected";
    public const string Error = "error";

    public string ToJson() => JsonSerializer.Serialize(this, RelayJsonContext.Default.RelayMessage);

    public static RelayMessage? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize(json, RelayJsonContext.Default.RelayMessage); }
        catch (JsonException) { return null; }
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelayMessage))]
public partial class RelayJsonContext : JsonSerializerContext;

public static class RelayUrls
{
    /// <summary>Normalizează adresa relay-ului: acceptă "host:port", "ws://..." sau "wss://...".</summary>
    public static Uri BaseUri(string relayAddress)
    {
        var a = relayAddress.Trim().TrimEnd('/');
        if (a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) a = "ws://" + a[7..];
        else if (a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) a = "wss://" + a[8..];
        else if (!a.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            a = "ws://" + a;
        return new Uri(a + "/");
    }

    private static Uri Build(string relayAddress, string path, string? key)
    {
        var q = string.IsNullOrEmpty(key) ? "" : "?k=" + Uri.EscapeDataString(key);
        return new Uri(BaseUri(relayAddress), path + q);
    }

    public static Uri Host(string relay, string? key) => Build(relay, "ws/host", key);
    public static Uri Connect(string relay, string? key, string id) => Build(relay, "ws/connect/" + Uri.EscapeDataString(id), key);
    public static Uri Session(string relay, string? key, string token) => Build(relay, "ws/session/" + Uri.EscapeDataString(token), key);
}
