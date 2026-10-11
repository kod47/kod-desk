using System.Text;

namespace KodDesk.Core;

/// <summary>
/// Relay-ul încorporat în aplicație la compilare.
/// Valorile vin din RelayConfig.Local.cs (generat cu tools/set-relay.ps1, exclus din git) și sunt
/// mascate (XOR cu o mască aleatoare), ca să nu apară ca text în executabil sau în fișierele de setări.
/// Dacă fișierul local lipsește, adresa și cheia se introduc din Setări.
/// </summary>
public static partial class RelayConfig
{
    private static readonly string? s_address;
    private static readonly string? s_key;

    static RelayConfig()
    {
        string? address = null, key = null;
        LoadEmbedded(ref address, ref key);
        s_address = address;
        s_key = key;
    }

    static partial void LoadEmbedded(ref string? address, ref string? key);

    /// <summary>Doar pentru teste: ignoră relay-ul încorporat.</summary>
    public static bool UseEmbedded { get; set; } = true;

    public static bool IsEmbedded => UseEmbedded && !string.IsNullOrEmpty(s_address);

    public static string Address(string configured) => IsEmbedded ? s_address! : configured;

    public static string Key(string configured) => IsEmbedded ? s_key ?? "" : configured;

    private static string Reveal(byte[] data, byte[] mask)
    {
        var plain = new byte[data.Length];
        for (int i = 0; i < data.Length; i++) plain[i] = (byte)(data[i] ^ mask[i]);
        return Encoding.UTF8.GetString(plain);
    }
}
