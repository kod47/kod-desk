using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KodDesk.Protocol;

namespace KodDesk.Relay;

public sealed class HostConnection(string id, WebSocket ws)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string Id { get; } = id;
    public WebSocket Socket { get; } = ws;

    public async Task SendAsync(RelayMessage msg, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try { await WebSocketText.SendAsync(Socket, msg, ct); }
        finally { _sendLock.Release(); }
    }
}

/// <summary>
/// Ține evidența host-urilor online și a asocierii ID -> hash(secret), salvată pe disc,
/// astfel încât un ID să nu poată fi preluat de alt calculator.
/// </summary>
public sealed class HostRegistry
{
    private readonly ConcurrentDictionary<string, HostConnection> _online = new();
    private readonly Dictionary<string, string> _claims;
    private readonly string _file;
    private readonly Lock _lock = new();

    public HostRegistry(IConfiguration config)
    {
        var dir = config["DATA_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "hosts.json");
        _claims = File.Exists(_file)
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_file)) ?? new()
            : new();
    }

    public enum ClaimResult { Ok, WrongSecret, NewIdRefused }

    /// <param name="allowNewId">Apelat doar pentru un ID nou; poate refuza (limite).</param>
    public ClaimResult TryClaim(string id, string secret, Func<int, bool> allowNewId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
        lock (_lock)
        {
            if (_claims.TryGetValue(id, out var existing))
                return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(existing), Encoding.ASCII.GetBytes(hash))
                    ? ClaimResult.Ok
                    : ClaimResult.WrongSecret;
            if (!allowNewId(_claims.Count)) return ClaimResult.NewIdRefused;
            _claims[id] = hash;
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_claims));
            File.Move(tmp, _file, overwrite: true);
            return ClaimResult.Ok;
        }
    }

    public void SetOnline(HostConnection conn)
    {
        HostConnection? old = null;
        _online.AddOrUpdate(conn.Id, conn, (_, prev) => { old = prev; return conn; });
        // O conexiune veche (ex. rețea căzută) este înlocuită de cea nouă.
        old?.Socket.Abort();
    }

    /// <summary>Returnează true dacă această conexiune era cea activă.</summary>
    public bool SetOffline(HostConnection conn) =>
        _online.TryRemove(new KeyValuePair<string, HostConnection>(conn.Id, conn));

    public bool TryGetOnline(string id, [NotNullWhen(true)] out HostConnection? conn) => _online.TryGetValue(id, out conn);
}

public sealed class PendingSession
{
    public TaskCompletionSource<WebSocket> HostSocket { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class SessionBroker
{
    private readonly ConcurrentDictionary<string, PendingSession> _sessions = new();

    public (string token, PendingSession session) Create()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var s = new PendingSession();
        _sessions[token] = s;
        return (token, s);
    }

    public bool TryGet(string token, [NotNullWhen(true)] out PendingSession? s) => _sessions.TryGetValue(token, out s);

    public void Remove(string token) => _sessions.TryRemove(token, out _);
}

public static class Pipe
{
    /// <summary>
    /// Retransmite mesajele în ambele sensuri până când una din părți închide sau expiră durata maximă.
    /// <paramref name="bytesPerSecond"/> limitează viteza medie pe fiecare sens (0 = fără limită).
    /// </summary>
    public static async Task<(long aToB, long bToA)> RunAsync(WebSocket a, WebSocket b, TimeSpan maxDuration,
        long bytesPerSecond, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (maxDuration > TimeSpan.Zero) cts.CancelAfter(maxDuration);
        var t1 = CopyAsync(a, b, bytesPerSecond, cts.Token);
        var t2 = CopyAsync(b, a, bytesPerSecond, cts.Token);
        await Task.WhenAny(t1, t2);
        cts.Cancel();
        long ab = 0, ba = 0;
        try { ab = await t1; } catch { }
        try { ba = await t2; } catch { }
        return (ab, ba);
    }

    private static async Task<long> CopyAsync(WebSocket from, WebSocket to, long bytesPerSecond, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long total = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long budgetBytes = 0;
        try
        {
            while (true)
            {
                ValueWebSocketReceiveResult r;
                try { r = await from.ReceiveAsync(buf.AsMemory(), ct); }
                catch (WebSocketException) { break; }
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    try { await to.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct); } catch { }
                    break;
                }
                await to.SendAsync(buf.AsMemory(0, r.Count), r.MessageType, r.EndOfMessage, ct);
                total += r.Count;
                if (bytesPerSecond > 0)
                {
                    // Limitare de viteză (rafale de cel mult ~1 s): dacă am trimis mai mult decât permite viteza, așteptăm.
                    budgetBytes += r.Count;
                    var ahead = TimeSpan.FromSeconds((double)budgetBytes / bytesPerSecond) - clock.Elapsed;
                    if (ahead < TimeSpan.FromSeconds(-1))
                    {
                        budgetBytes = 0;
                        clock.Restart();
                    }
                    else if (ahead > TimeSpan.FromMilliseconds(5))
                    {
                        await Task.Delay(ahead, ct);
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return total;
    }
}
