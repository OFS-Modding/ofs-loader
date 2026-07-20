using System.Buffers.Binary;
using System.Text;

namespace OFS.Runtime.Entry;

internal static class NetworkEnvelopeCodec
{
    internal const ushort MessageId = 0x4F46;
    internal const int MaxPayloadBytes = 32 * 1024;
    private const int FixedHeaderBytes = 13;
    private const byte ProtocolVersion = 1;
    private const uint Magic = 0x4D53464F; // OFSM in little endian.
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(string qualifiedChannelId, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedChannelId);
        if (payload.Length > MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(
                nameof(payload), $"Network payload exceeds {MaxPayloadBytes} bytes.");
        var channel = Utf8.GetBytes(qualifiedChannelId);
        if (channel.Length is 0 or > 255)
            throw new ArgumentOutOfRangeException(
                nameof(qualifiedChannelId), "Qualified channel id must encode to 1-255 UTF-8 bytes.");

        var frame = new byte[checked(FixedHeaderBytes + channel.Length + payload.Length)];
        var span = frame.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, MessageId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[2..], Magic);
        span[6] = ProtocolVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(span[7..], (ushort)channel.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[9..], payload.Length);
        channel.CopyTo(span[FixedHeaderBytes..]);
        payload.CopyTo(span[(FixedHeaderBytes + channel.Length)..]);
        return frame;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> frame,
        out string qualifiedChannelId,
        out byte[] payload,
        out string error)
    {
        qualifiedChannelId = string.Empty;
        payload = [];
        error = string.Empty;
        if (frame.Length < FixedHeaderBytes)
            return Fail("Envelope is shorter than its fixed header.", out error);
        if (BinaryPrimitives.ReadUInt16LittleEndian(frame) != MessageId)
            return Fail("Envelope uses a different Mirror message id.", out error);
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame[2..]) != Magic)
            return Fail("Envelope magic is invalid.", out error);
        if (frame[6] != ProtocolVersion)
            return Fail($"Envelope protocol {frame[6]} is unsupported.", out error);

        var channelLength = BinaryPrimitives.ReadUInt16LittleEndian(frame[7..]);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame[9..]);
        if (channelLength is 0 or > 255)
            return Fail("Envelope channel length is invalid.", out error);
        if (payloadLength is < 0 or > MaxPayloadBytes)
            return Fail("Envelope payload length is invalid.", out error);
        if (frame.Length != FixedHeaderBytes + channelLength + payloadLength)
            return Fail("Envelope length does not match its header.", out error);

        try
        {
            qualifiedChannelId = Utf8.GetString(frame.Slice(FixedHeaderBytes, channelLength));
        }
        catch (DecoderFallbackException)
        {
            return Fail("Envelope channel id is not valid UTF-8.", out error);
        }
        payload = frame[(FixedHeaderBytes + channelLength)..].ToArray();
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
