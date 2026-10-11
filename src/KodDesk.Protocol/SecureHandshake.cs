using System.Security.Cryptography;
using System.Text;

namespace KodDesk.Protocol;

/// <summary>
/// Handshake cap-la-cap între host și viewer, prin relay:
///   1. Host   -> Viewer: HostHello   { versiune, id, nume, cheie publică ECDH, nonceH }
///   2. Viewer -> Host:   ViewerHello { cheie publică ECDH, nonceV, dovadăV = HMAC(cheieParolă, "viewer" || transcript) }
///   3. Host   -> Viewer: HostAuth    { dovadăH = HMAC(cheieParolă, "host" || transcript) }  sau AuthFailed
/// Cheile de sesiune: HKDF(secretECDH || cheieParolă, salt = transcript).
/// Relay-ul nu cunoaște parola, deci nu poate citi și nici falsifica traficul.
/// </summary>
public static class SecureHandshake
{
    private const int NonceSize = 32;

    /// <summary>Derivă cheia din parolă. Host-ul păstrează doar această cheie, nu parola.</summary>
    public static byte[] DerivePasswordKey(string password, string hostId)
    {
        var salt = Encoding.UTF8.GetBytes("KodDesk/v1/" + NormalizeId(hostId));
        return Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 200_000, HashAlgorithmName.SHA256, 32);
    }

    public static string NormalizeId(string id) => new(id.Where(char.IsDigit).ToArray());

    public sealed record HostResult(bool Success, string? Error);

    /// <param name="PasswordKey">Cheia derivată din parolă (poate fi memorată pentru conectări ulterioare).</param>
    public sealed record ViewerResult(string HostId, string HostName, byte[] PasswordKey);

    public static async Task<HostResult> RunHostAsync(MessageChannel ch, string hostId, string hostName,
        byte[]? passwordKey, CancellationToken ct)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hostPub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var nonceH = RandomNumberGenerator.GetBytes(NonceSize);

        var hello = new PacketWriter()
            .U16(ProtocolConstants.Version)
            .Str(hostId)
            .Str(hostName)
            .Bytes(hostPub)
            .Bytes(nonceH);
        await ch.SendDirectAsync(MessageType.HostHello, hello.Span.ToArray(), ct).ConfigureAwait(false);

        var msg = await ch.ReceiveAsync(ct).ConfigureAwait(false)
            ?? throw new IOException("Conexiune închisă în timpul autentificării");
        if (msg.Type != MessageType.ViewerHello) throw new InvalidDataException("Mesaj neașteptat: " + msg.Type);

        byte[] viewerPub, nonceV, proofV;
        {
            var r = new PacketReader(msg.Payload.Span);
            viewerPub = r.Bytes().ToArray();
            nonceV = r.Bytes().ToArray();
            proofV = r.Bytes().ToArray();
        }

        if (passwordKey == null)
        {
            await SendFailAsync(ch, "Pe acest calculator nu este setată o parolă pentru acces.", ct).ConfigureAwait(false);
            return new HostResult(false, "parolă nesetată");
        }

        var transcript = Transcript(hostId, hostPub, nonceH, viewerPub, nonceV);
        var expected = Proof(passwordKey, "viewer", transcript);
        if (!CryptographicOperations.FixedTimeEquals(expected, proofV))
        {
            await SendFailAsync(ch, "Parolă greșită.", ct).ConfigureAwait(false);
            return new HostResult(false, "parolă greșită");
        }

        var proofH = Proof(passwordKey, "host", transcript);
        await ch.SendDirectAsync(MessageType.HostAuth, new PacketWriter().Bytes(proofH).Span.ToArray(), ct).ConfigureAwait(false);

        var shared = DeriveShared(ecdh, viewerPub);
        var (h2v, v2h) = SessionKeys(shared, passwordKey, transcript);
        ch.EnableEncryption(sendKey: h2v, recvKey: v2h);
        return new HostResult(true, null);
    }

    /// <param name="expectedHostId">ID-ul la care ne-am conectat; host-ul trebuie să se prezinte cu același ID.</param>
    /// <param name="passwordKey">Cheia memorată, sau null pentru a o deriva din <paramref name="password"/>.</param>
    public static async Task<ViewerResult> RunViewerAsync(MessageChannel ch, string expectedHostId, string? password,
        byte[]? passwordKey, CancellationToken ct)
    {
        var msg = await ch.ReceiveAsync(ct).ConfigureAwait(false)
            ?? throw new IOException("Conexiune închisă de calculatorul la distanță");
        if (msg.Type != MessageType.HostHello) throw new InvalidDataException("Mesaj neașteptat: " + msg.Type);

        ushort version;
        string hostId, hostName;
        byte[] hostPub, nonceH;
        {
            var r = new PacketReader(msg.Payload.Span);
            version = r.U16();
            hostId = r.Str();
            hostName = r.Str();
            hostPub = r.Bytes().ToArray();
            nonceH = r.Bytes().ToArray();
        }
        if (version != ProtocolConstants.Version)
            throw new InvalidDataException($"Versiune de protocol incompatibilă (la distanță {version}, local {ProtocolConstants.Version}). Actualizați aplicația.");

        if (NormalizeId(hostId) != NormalizeId(expectedHostId))
            throw new AuthenticationFailedException("Calculatorul la distanță s-a prezentat cu alt ID.");
        passwordKey ??= DerivePasswordKey(password ?? "", hostId);
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var viewerPub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var nonceV = RandomNumberGenerator.GetBytes(NonceSize);
        var transcript = Transcript(hostId, hostPub, nonceH, viewerPub, nonceV);
        var proofV = Proof(passwordKey, "viewer", transcript);

        var vh = new PacketWriter().Bytes(viewerPub).Bytes(nonceV).Bytes(proofV);
        await ch.SendDirectAsync(MessageType.ViewerHello, vh.Span.ToArray(), ct).ConfigureAwait(false);

        msg = await ch.ReceiveAsync(ct).ConfigureAwait(false)
            ?? throw new IOException("Conexiune închisă în timpul autentificării");
        if (msg.Type == MessageType.AuthFailed)
        {
            var reason = new PacketReader(msg.Payload.Span).Str();
            throw new AuthenticationFailedException(reason);
        }
        if (msg.Type != MessageType.HostAuth) throw new InvalidDataException("Mesaj neașteptat: " + msg.Type);

        var proofH = new PacketReader(msg.Payload.Span).Bytes().ToArray();
        if (!CryptographicOperations.FixedTimeEquals(Proof(passwordKey, "host", transcript), proofH))
            throw new AuthenticationFailedException("Calculatorul la distanță nu a putut fi verificat (posibil atac).");

        var shared = DeriveShared(ecdh, hostPub);
        var (h2v, v2h) = SessionKeys(shared, passwordKey, transcript);
        ch.EnableEncryption(sendKey: v2h, recvKey: h2v);
        return new ViewerResult(hostId, hostName, passwordKey);
    }

    private static async Task SendFailAsync(MessageChannel ch, string reason, CancellationToken ct)
    {
        try
        {
            await ch.SendDirectAsync(MessageType.AuthFailed, new PacketWriter().Str(reason).Span.ToArray(), ct).ConfigureAwait(false);
        }
        catch { }
    }

    private static byte[] Transcript(string hostId, byte[] hostPub, byte[] nonceH, byte[] viewerPub, byte[] nonceV)
    {
        var w = new PacketWriter()
            .Str("KodDesk/v1")
            .Str(NormalizeId(hostId))
            .Bytes(hostPub).Bytes(nonceH)
            .Bytes(viewerPub).Bytes(nonceV);
        return SHA256.HashData(w.Span);
    }

    private static byte[] Proof(byte[] passwordKey, string role, byte[] transcript)
    {
        var data = new PacketWriter().Str(role).Raw(transcript);
        return HMACSHA256.HashData(passwordKey, data.Span);
    }

    private static byte[] DeriveShared(ECDiffieHellman ecdh, byte[] otherPub)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherPub, out _);
        return ecdh.DeriveRawSecretAgreement(other.PublicKey);
    }

    private static (byte[] h2v, byte[] v2h) SessionKeys(byte[] shared, byte[] passwordKey, byte[] transcript)
    {
        var ikm = new byte[shared.Length + passwordKey.Length];
        shared.CopyTo(ikm, 0);
        passwordKey.CopyTo(ikm, shared.Length);
        var h2v = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, transcript, Encoding.UTF8.GetBytes("host->viewer"));
        var v2h = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, transcript, Encoding.UTF8.GetBytes("viewer->host"));
        CryptographicOperations.ZeroMemory(ikm);
        return (h2v, v2h);
    }
}

public sealed class AuthenticationFailedException(string message) : Exception(message);
