using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;

namespace KodDesk.Protocol;

/// <summary>
/// Canal de mesaje peste un WebSocket. Fiecare mesaj WebSocket binar = [tip][payload].
/// După handshake, mesajele sunt criptate AES-256-GCM cu nonce implicit (contor per direcție).
/// Trimiterea se face printr-o buclă unică, cu două cozi: prioritate mare (control, input)
/// și prioritate mică (video, fișiere).
/// </summary>
public sealed class MessageChannel : IAsyncDisposable
{
    private const int TagSize = 16;

    private readonly WebSocket _ws;
    private readonly ConcurrentQueue<byte[]> _high = new();
    private readonly ConcurrentQueue<byte[]> _low = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private Task? _sendLoop;

    private AesGcm? _sendAes, _recvAes;
    private ulong _sendCounter, _recvCounter;

    private byte[] _recvBuf = new byte[64 * 1024];
    private byte[] _plainBuf = new byte[64 * 1024];
    private int _lowPending;

    public MessageChannel(WebSocket ws) => _ws = ws;

    public bool IsEncrypted => _sendAes != null;

    /// <summary>Numărul de mesaje cu prioritate mică încă netrimise.</summary>
    public int LowPriorityPending => Volatile.Read(ref _lowPending);

    /// <summary>Total octeți trimiși/primiți (pentru statistici).</summary>
    public long BytesSent { get; private set; }
    public long BytesReceived { get; private set; }

    /// <summary>Se declanșează când bucla de trimitere se oprește din cauza unei erori.</summary>
    public event Action<Exception>? SendFailed;

    public void EnableEncryption(byte[] sendKey, byte[] recvKey)
    {
        if (_sendLoop != null) throw new InvalidOperationException("Criptarea trebuie activată înainte de pornirea buclei de trimitere.");
        _sendAes = new AesGcm(sendKey, TagSize);
        _recvAes = new AesGcm(recvKey, TagSize);
    }

    /// <summary>Trimite direct (folosit doar în handshake, înainte de StartSending).</summary>
    public async Task SendDirectAsync(MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (_sendLoop != null) throw new InvalidOperationException("Bucla de trimitere rulează deja.");
        var msg = Frame(type, payload.Span);
        await _ws.SendAsync(Seal(msg), WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
    }

    public void StartSending()
    {
        _sendLoop ??= Task.Run(SendLoopAsync);
    }

    public void Send(MessageType type, ReadOnlySpan<byte> payload, bool lowPriority = false)
    {
        if (_cts.IsCancellationRequested) return;
        var msg = Frame(type, payload);
        if (lowPriority)
        {
            Interlocked.Increment(ref _lowPending);
            _low.Enqueue(msg);
        }
        else
        {
            _high.Enqueue(msg);
        }
        _signal.Release();
    }

    public void Send(MessageType type) => Send(type, ReadOnlySpan<byte>.Empty);
    public void Send(MessageType type, PacketWriter w, bool lowPriority = false) => Send(type, w.Span, lowPriority);

    private static byte[] Frame(MessageType type, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[1 + payload.Length];
        buf[0] = (byte)type;
        payload.CopyTo(buf.AsSpan(1));
        return buf;
    }

    private static void MakeNonce(Span<byte> nonce, ulong counter)
    {
        nonce.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], counter);
    }

    private byte[] Seal(byte[] plain)
    {
        if (_sendAes == null) return plain;
        var output = new byte[plain.Length + TagSize];
        Span<byte> nonce = stackalloc byte[12];
        MakeNonce(nonce, _sendCounter++);
        _sendAes.Encrypt(nonce, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length, TagSize));
        return output;
    }

    private async Task SendLoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                if (!_high.TryDequeue(out var msg))
                {
                    if (!_low.TryDequeue(out msg)) continue;
                    Interlocked.Decrement(ref _lowPending);
                }
                var sealedMsg = Seal(msg);
                await _ws.SendAsync(sealedMsg, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                BytesSent += sealedMsg.Length;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _cts.Cancel();
            SendFailed?.Invoke(ex);
        }
    }

    /// <summary>
    /// Primește următorul mesaj. Memoria returnată este validă doar până la următorul apel.
    /// Returnează null când conexiunea s-a închis.
    /// </summary>
    public async ValueTask<Message?> ReceiveAsync(CancellationToken ct)
    {
        int count = 0;
        while (true)
        {
            if (count == _recvBuf.Length)
            {
                if (_recvBuf.Length >= ProtocolConstants.MaxMessageSize)
                    throw new InvalidDataException("Mesaj prea mare");
                Array.Resize(ref _recvBuf, _recvBuf.Length * 2);
            }
            ValueWebSocketReceiveResult r;
            try
            {
                r = await _ws.ReceiveAsync(_recvBuf.AsMemory(count), ct).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                return null;
            }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            count += r.Count;
            if (r.EndOfMessage)
            {
                if (r.MessageType != WebSocketMessageType.Binary) { count = 0; continue; }
                break;
            }
        }
        BytesReceived += count;

        ReadOnlyMemory<byte> plain;
        if (_recvAes != null)
        {
            if (count < TagSize + 1) throw new CryptographicException("Mesaj criptat invalid");
            int len = count - TagSize;
            if (_plainBuf.Length < len) _plainBuf = new byte[Math.Max(len, _plainBuf.Length * 2)];
            Span<byte> nonce = stackalloc byte[12];
            MakeNonce(nonce, _recvCounter++);
            _recvAes.Decrypt(nonce, _recvBuf.AsSpan(0, len), _recvBuf.AsSpan(len, TagSize), _plainBuf.AsSpan(0, len));
            plain = _plainBuf.AsMemory(0, len);
        }
        else
        {
            if (count < 1) throw new InvalidDataException("Mesaj gol");
            plain = _recvBuf.AsMemory(0, count);
        }
        return new Message((MessageType)plain.Span[0], plain[1..]);
    }

    public async Task CloseAsync()
    {
        _cts.Cancel();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        if (_sendLoop != null)
        {
            try { await _sendLoop.ConfigureAwait(false); } catch { }
        }
        _ws.Dispose();
        _sendAes?.Dispose();
        _recvAes?.Dispose();
        _cts.Dispose();
    }
}

public readonly record struct Message(MessageType Type, ReadOnlyMemory<byte> Payload);
