namespace KodDesk.Protocol;

public sealed record MonitorInfo(string Name, int Left, int Top, int Width, int Height, bool Primary);

public sealed record SessionInfo(string HostName, IReadOnlyList<MonitorInfo> Monitors, int CurrentMonitor)
{
    public PacketWriter Write()
    {
        var w = new PacketWriter().Str(HostName).U8((byte)Monitors.Count);
        foreach (var m in Monitors)
            w.Str(m.Name).I32(m.Left).I32(m.Top).I32(m.Width).I32(m.Height).Bool(m.Primary);
        return w.U8((byte)CurrentMonitor);
    }

    public static SessionInfo Read(ReadOnlySpan<byte> s)
    {
        var r = new PacketReader(s);
        var name = r.Str();
        int n = r.U8();
        var list = new List<MonitorInfo>(n);
        for (int i = 0; i < n; i++)
            list.Add(new MonitorInfo(r.Str(), r.I32(), r.I32(), r.I32(), r.I32(), r.Bool()));
        return new SessionInfo(name, list, r.U8());
    }
}

public sealed record VideoFormat(int Width, int Height, string Codec, bool Yuv444, bool FullRange, string EncoderName, int MonitorIndex)
{
    public PacketWriter Write() => new PacketWriter()
        .U16((ushort)Width).U16((ushort)Height).Str(Codec).Bool(Yuv444).Bool(FullRange).Str(EncoderName).U8((byte)MonitorIndex);

    public static VideoFormat Read(ReadOnlySpan<byte> s)
    {
        var r = new PacketReader(s);
        return new VideoFormat(r.U16(), r.U16(), r.Str(), r.Bool(), r.Bool(), r.Str(), r.U8());
    }
}

[Flags]
public enum VideoFrameFlags : byte
{
    None = 0,
    Keyframe = 1,
}

public static class VideoFrameHeader
{
    public const int Size = 4 + 1 + 8;

    public static void Write(Span<byte> dst, uint seq, VideoFrameFlags flags, long timestampMs)
    {
        var w = new PacketWriter(Size).U32(seq).U8((byte)flags).I64(timestampMs);
        w.Span.CopyTo(dst);
    }

    public static (uint seq, VideoFrameFlags flags, long timestampMs, ReadOnlyMemory<byte> data) Read(ReadOnlyMemory<byte> payload)
    {
        var r = new PacketReader(payload.Span);
        var seq = r.U32();
        var flags = (VideoFrameFlags)r.U8();
        var ts = r.I64();
        return (seq, flags, ts, payload[Size..]);
    }
}

public sealed record QualitySettings(int BitrateKbps, int MaxFps)
{
    public PacketWriter Write() => new PacketWriter().I32(BitrateKbps).U8((byte)MaxFps);

    public static QualitySettings Read(ReadOnlySpan<byte> s)
    {
        var r = new PacketReader(s);
        return new QualitySettings(r.I32(), r.U8());
    }
}
