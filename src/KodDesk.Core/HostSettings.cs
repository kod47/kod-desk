using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KodDesk.Protocol;

namespace KodDesk.Core;

/// <summary>
/// Setările calculatorului controlat (ID, relay, parola de acces).
/// Fișier: %ProgramData%\KodDesk\host.json. După instalarea serviciului, folderul este accesibil
/// doar pentru SYSTEM și Administratori.
/// </summary>
public sealed class HostSettings
{
    /// <summary>ID-ul acestui calculator (9 cifre), generat la prima pornire.</summary>
    public string HostId { get; set; } = "";

    /// <summary>Secret folosit la înregistrarea pe relay; împiedică preluarea ID-ului de alt calculator.</summary>
    public string RelaySecret { get; set; } = "";

    /// <summary>Adresa relay-ului, ex. "wss://kod-desk.exemplu.ro".</summary>
    public string RelayAddress { get; set; } = "";

    public string RelayKey { get; set; } = "";

    /// <summary>Cheia derivată din parola de acces, protejată cu DPAPI (nivel mașină).</summary>
    public string? PasswordKeyProtected { get; set; }

    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrEmpty(PasswordKeyProtected);

    /// <summary>Relay-ul efectiv: cel încorporat în aplicație, altfel cel din setări.</summary>
    [JsonIgnore]
    public string ActiveRelayAddress => RelayConfig.Address(RelayAddress);

    [JsonIgnore]
    public string ActiveRelayKey => RelayConfig.Key(RelayKey);

    [JsonIgnore]
    public bool IsRelayConfigured => !string.IsNullOrWhiteSpace(ActiveRelayAddress);

    public static string FilePath => Path.Combine(AppPaths.DataDir, "host.json");

    /// <summary>Fișierul folosit de versiunea 0.1 (setări comune host + viewer).</summary>
    internal static string LegacyFilePath => Path.Combine(AppPaths.DataDir, "settings.json");

    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static HostSettings Load()
    {
        HostSettings s;
        try
        {
            var path = File.Exists(FilePath) ? FilePath : File.Exists(LegacyFilePath) ? LegacyFilePath : null;
            s = path != null ? JsonSerializer.Deserialize<HostSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (Exception ex)
        {
            Log.Error("Nu pot citi setările calculatorului", ex);
            throw;
        }

        bool changed = !File.Exists(FilePath);
        if (RelayConfig.IsEmbedded && (s.RelayAddress.Length > 0 || s.RelayKey.Length > 0))
        {
            // Relay-ul e încorporat în aplicație: nu mai păstrăm cheia în fișier.
            s.RelayAddress = "";
            s.RelayKey = "";
            changed = true;
        }
        if (string.IsNullOrEmpty(s.HostId))
        {
            s.HostId = RandomNumberGenerator.GetInt32(100_000_000, 1_000_000_000).ToString();
            changed = true;
        }
        if (string.IsNullOrEmpty(s.RelaySecret))
        {
            s.RelaySecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            changed = true;
        }
        if (changed) s.Save();
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void SetPassword(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            PasswordKeyProtected = null;
            return;
        }
        var key = SecureHandshake.DerivePasswordKey(password, HostId);
        PasswordKeyProtected = Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.LocalMachine));
        CryptographicOperations.ZeroMemory(key);
    }

    public byte[]? GetPasswordKey()
    {
        if (string.IsNullOrEmpty(PasswordKeyProtected)) return null;
        try
        {
            return ProtectedData.Unprotect(Convert.FromBase64String(PasswordKeyProtected), null, DataProtectionScope.LocalMachine);
        }
        catch (Exception ex)
        {
            Log.Error("Nu pot decripta cheia parolei", ex);
            return null;
        }
    }

    public static string FormatId(string id)
    {
        id = SecureHandshake.NormalizeId(id);
        return id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;
    }
}
