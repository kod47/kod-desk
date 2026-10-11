using System.Net.WebSockets;
using System.Text;

namespace KodDesk.Protocol;

public static class WebSocketText
{
    private const int MaxTextSize = 64 * 1024;

    public static Task SendAsync(WebSocket ws, RelayMessage msg, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJson()), WebSocketMessageType.Text, true, ct);

    /// <summary>Citește un mesaj text de la relay. Returnează null dacă s-a închis conexiunea.</summary>
    public static async Task<RelayMessage?> ReceiveAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[4096];
        int count = 0;
        while (true)
        {
            if (count == buf.Length)
            {
                if (buf.Length >= MaxTextSize) throw new InvalidDataException("Mesaj text prea mare");
                Array.Resize(ref buf, buf.Length * 2);
            }
            var r = await ws.ReceiveAsync(buf.AsMemory(count), ct).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            count += r.Count;
            if (!r.EndOfMessage) continue;
            if (r.MessageType != WebSocketMessageType.Text) { count = 0; continue; }
            return RelayMessage.FromJson(Encoding.UTF8.GetString(buf, 0, count))
                ?? throw new InvalidDataException("Mesaj invalid de la relay");
        }
    }
}
