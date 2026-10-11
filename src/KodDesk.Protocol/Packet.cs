using System.Buffers.Binary;
using System.Text;

namespace KodDesk.Protocol;

/// <summary>Construiește un payload binar (little-endian).</summary>
public sealed class PacketWriter
{
    private byte[] _buf;
    private int _pos;

    public PacketWriter(int capacity = 256) => _buf = new byte[capacity];

    public int Length => _pos;
    public ReadOnlySpan<byte> Span => _buf.AsSpan(0, _pos);

    private Span<byte> Grow(int n)
    {
        if (_pos + n > _buf.Length)
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _pos + n));
        var s = _buf.AsSpan(_pos, n);
        _pos += n;
        return s;
    }

    public PacketWriter U8(byte v) { Grow(1)[0] = v; return this; }
    public PacketWriter Bool(bool v) => U8(v ? (byte)1 : (byte)0);
    public PacketWriter U16(ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), v); return this; }
    public PacketWriter I16(short v) { BinaryPrimitives.WriteInt16LittleEndian(Grow(2), v); return this; }
    public PacketWriter U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), v); return this; }
    public PacketWriter I32(int v) { BinaryPrimitives.WriteInt32LittleEndian(Grow(4), v); return this; }
    public PacketWriter I64(long v) { BinaryPrimitives.WriteInt64LittleEndian(Grow(8), v); return this; }

    /// <summary>Octeți cu prefix de lungime (u32).</summary>
    public PacketWriter Bytes(ReadOnlySpan<byte> v) { U32((uint)v.Length); v.CopyTo(Grow(v.Length)); return this; }

    /// <summary>Octeți fără prefix (de obicei la final de mesaj).</summary>
    public PacketWriter Raw(ReadOnlySpan<byte> v) { v.CopyTo(Grow(v.Length)); return this; }

    public PacketWriter Str(string v)
    {
        var n = Encoding.UTF8.GetByteCount(v);
        if (n > ushort.MaxValue) throw new ArgumentException("String prea lung");
        U16((ushort)n);
        Encoding.UTF8.GetBytes(v, Grow(n));
        return this;
    }
}

/// <summary>Citește un payload binar (little-endian).</summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _s;
    private int _pos;

    public PacketReader(ReadOnlySpan<byte> s) { _s = s; _pos = 0; }

    public readonly int Remaining => _s.Length - _pos;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || _pos + n > _s.Length) throw new InvalidDataException("Mesaj trunchiat");
        var r = _s.Slice(_pos, n);
        _pos += n;
        return r;
    }

    public byte U8() => Take(1)[0];
    public bool Bool() => U8() != 0;
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ReadOnlySpan<byte> Bytes() => Take((int)U32());
    public ReadOnlySpan<byte> Rest() => Take(Remaining);
    public string Str() => Encoding.UTF8.GetString(Take(U16()));
}
