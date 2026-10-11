using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KodDesk.Protocol;

namespace KodDesk.Core;

/// <summary>
/// Setările utilizatorului când controlează alte calculatoare: relay, calitate, istoric, parole memorate.
/// Fișier: %LocalAppData%\KodDesk\viewer.json.
/// </summary>
public sealed class ViewerSettings
{
    public string RelayAddress { get; set; } = "";
    public string RelayKey { get; set; } = "";
    public int BitrateKbps { get; set; } = 12000;
    public int MaxFps { get; set; } = 60;
    public List<string> RecentIds { get; set; } = [];

    /// <summary>
    /// ID -> cheia derivată din parolă (nu parola în clar), criptată cu DPAPI pentru utilizatorul curent.
    /// </summary>
    public Dictionary<string, string> SavedKeys { get; set; } = [];

    [JsonIgnore]
    public string ActiveRelayAddress => RelayConfig.Address(RelayAddress);

    [JsonIgnore]
    public string ActiveRelayKey => RelayConfig.Key(RelayKey);

    [JsonIgnore]
    public bool IsRelayConfigured => !string.IsNullOrWhiteSpace(ActiveRelayAddress);

    private static string FilePath => Path.Combine(AppPaths.UserDataDir, "viewer.json");

    public static ViewerSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(FilePath), HostSettings.Json) ?? new();
                if (RelayConfig.IsEmbedded && (loaded.RelayAddress.Length > 0 || loaded.RelayKey.Length > 0))
                {
                    // Relay-ul e încorporat în aplicație: nu mai păstrăm cheia în fișier.
                    loaded.RelayAddress = "";
                    loaded.RelayKey = "";
                    loaded.Save();
                }
                return loaded;
            }

            // Prima pornire după actualizare: preluăm relay-ul, calitatea și istoricul din versiunea 0.1.
            var s = new ViewerSettings();
            foreach (var path in new[] { HostSettings.LegacyFilePath, HostSettings.FilePath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var old = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(path), HostSettings.Json);
                    if (old != null && (old.RelayAddress.Length > 0 || old.RecentIds.Count > 0))
                    {
                        s = old;
                        s.SavedKeys = [];
                        if (RelayConfig.IsEmbedded)
                        {
                            s.RelayAddress = "";
                            s.RelayKey = "";
                        }
                        break;
                    }
                }
                catch (UnauthorizedAccessException) { }
            }
            s.Save();
            return s;
        }
        catch (Exception ex)
        {
            Log.Error("Nu pot citi setările de conectare", ex);
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.UserDataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, HostSettings.Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void AddRecent(string id)
    {
        id = SecureHandshake.NormalizeId(id);
        RecentIds.Remove(id);
        RecentIds.Insert(0, id);
        if (RecentIds.Count > 10) RecentIds.RemoveRange(10, RecentIds.Count - 10);
    }

    public bool HasSavedKey(string id) => SavedKeys.ContainsKey(SecureHandshake.NormalizeId(id));

    public byte[]? GetSavedKey(string id)
    {
        if (!SavedKeys.TryGetValue(SecureHandshake.NormalizeId(id), out var b64)) return null;
        try
        {
            return ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser);
        }
        catch (Exception ex)
        {
            Log.Warn("Nu pot decripta parola memorată", ex);
            return null;
        }
    }

    public void SetSavedKey(string id, byte[]? key)
    {
        id = SecureHandshake.NormalizeId(id);
        if (key == null)
            SavedKeys.Remove(id);
        else
            SavedKeys[id] = Convert.ToBase64String(ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser));
    }
}
