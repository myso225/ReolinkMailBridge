using System.Buffers.Binary;
using System.Text;

namespace ReolinkMailBridge.Baichuan;

/// <summary>
/// One Baichuan message. Header (little-endian): magic 0x0abcdef0, msg id, body length, channel, stream type,
/// message number, response code, class, and for classes 0x6414 / 0x0000 a payload offset (= extension length).
/// The body is an optional "Extension" XML followed by the payload (usually XML).
/// </summary>
public sealed record BcMessage(
    uint MsgId,
    ushort MsgNum,
    ushort Class,
    ushort ResponseCode = 0,
    byte Channel = 0,
    byte StreamType = 0,
    byte[]? Extension = null,
    byte[]? Payload = null)
{
    public const uint Magic = 0x0abcdef0;
    public const uint MsgLogin = 1, MsgLogout = 2, MsgVersion = 80, MsgBatteryList = 252, MsgBattery = 253;

    public static bool HasPayloadOffset(ushort cls) => cls is 0x6414 or 0x0000;
    public static int HeaderLength(ushort cls) => HasPayloadOffset(cls) ? 24 : 20;

    public string ExtensionText => Extension is { Length: > 0 } e ? Encoding.UTF8.GetString(e) : "";
    public string PayloadText => Payload is { Length: > 0 } p ? Encoding.UTF8.GetString(p) : "";

    /// <summary>Serializes with extension and payload already encrypted by the caller.</summary>
    public byte[] ToBytes()
    {
        var ext = Extension ?? [];
        var payload = Payload ?? [];
        var headerLength = HeaderLength(Class);
        var buffer = new byte[headerLength + ext.Length + payload.Length];
        var s = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], MsgId);
        BinaryPrimitives.WriteUInt32LittleEndian(s[8..], (uint)(ext.Length + payload.Length));
        s[12] = Channel;
        s[13] = StreamType;
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], MsgNum);
        BinaryPrimitives.WriteUInt16LittleEndian(s[16..], ResponseCode);
        BinaryPrimitives.WriteUInt16LittleEndian(s[18..], Class);
        if (HasPayloadOffset(Class))
            BinaryPrimitives.WriteUInt32LittleEndian(s[20..], (uint)ext.Length);
        ext.CopyTo(s[headerLength..]);
        payload.CopyTo(s[(headerLength + ext.Length)..]);
        return buffer;
    }

    /// <summary>
    /// Takes one complete message off the front of <paramref name="buffer"/>, still encrypted.
    /// Returns null when more bytes are needed.
    /// </summary>
    public static BcMessage? TryRead(List<byte> buffer)
    {
        if (buffer.Count < 20) return null;
        var head = buffer.GetRange(0, Math.Min(24, buffer.Count)).ToArray();
        if (BinaryPrimitives.ReadUInt32LittleEndian(head) != Magic)
            throw new InvalidDataException($"Bad Baichuan magic {Convert.ToHexString(head, 0, 4)}");

        var cls = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(18));
        var headerLength = HeaderLength(cls);
        if (buffer.Count < headerLength) return null;
        var bodyLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));
        if (buffer.Count < headerLength + bodyLength) return null;

        var extLength = HasPayloadOffset(cls) ? (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(20)) : 0;
        var body = buffer.GetRange(headerLength, bodyLength).ToArray();
        buffer.RemoveRange(0, headerLength + bodyLength);

        return new BcMessage(
            BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4)),
            BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(14)),
            cls,
            BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(16)),
            head[12],
            head[13],
            body[..extLength],
            body[extLength..]);
    }
}
