namespace KodDesk.Protocol;

/// <summary>
/// Tipul mesajului; primul octet din fiecare mesaj WebSocket binar (după decriptare).
/// </summary>
public enum MessageType : byte
{
    // Handshake (necriptat, dar autentificat prin parolă)
    HostHello = 0x01,
    ViewerHello = 0x02,
    HostAuth = 0x03,
    AuthFailed = 0x04,

    // Control sesiune
    SessionInfo = 0x08,
    Ping = 0x09,
    Pong = 0x0A,
    Disconnect = 0x0B,

    // Video
    VideoFormat = 0x10,
    VideoFrame = 0x11,
    FrameAck = 0x12,
    RequestKeyframe = 0x13,
    SetQuality = 0x14,
    SelectMonitor = 0x15,

    // Input (viewer -> host)
    MouseMove = 0x20,
    MouseButton = 0x21,
    MouseWheel = 0x22,
    Key = 0x23,
    ReleaseAllKeys = 0x24,
    SecureAttention = 0x25,

    // Clipboard (în ambele sensuri)
    ClipboardText = 0x40,
    ClipboardImage = 0x41,
    ClipboardFiles = 0x42,

    // Fișiere (în ambele sensuri). Cine deține fișierele le „oferă” ca set; celălalt le citește prin fluxuri.
    FileStreamOpen = 0x50,
    FileStreamData = 0x51,
    FileStreamAck = 0x52,
    FileStreamClose = 0x53,
    FileStreamError = 0x54,

    // Fișiere (viewer -> host și răspunsuri)
    FileUpload = 0x58,
    FileUploadResult = 0x59,
    DirListRequest = 0x5A,
    DirListResponse = 0x5B,
    DownloadRequest = 0x5C,
    DownloadResponse = 0x5D,
}

public enum MouseButtonKind : byte
{
    Left = 0,
    Right = 1,
    Middle = 2,
    X1 = 3,
    X2 = 4,
}

public static class ProtocolConstants
{
    /// <summary>2: clipboard și transfer de fișiere.</summary>
    public const ushort Version = 2;

    /// <summary>Dimensiunea maximă a unui mesaj acceptat (protecție împotriva alocărilor uriașe).</summary>
    public const int MaxMessageSize = 64 * 1024 * 1024;
}
