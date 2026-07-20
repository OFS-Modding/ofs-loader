using System.Buffers.Binary;
using System.Text;

namespace OFS.Runtime.Entry;

internal enum NetworkRpcMessageKind : byte
{
    Request = 1,
    Success = 2,
    Error = 3,
}

internal readonly record struct NetworkRpcMessage(
    NetworkRpcMessageKind Kind,
    uint RequestId,
    byte[] Body);

internal static class NetworkRpcCodec
{
    internal const byte ProtocolVersion = 1;
    internal const int HeaderBytes = 10;
    internal const int MaximumBodyBytes = NetworkEnvelopeCodec.MaxPayloadBytes - HeaderBytes;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(
        NetworkRpcMessageKind kind,
        uint requestId,
        ReadOnlySpan<byte> body)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (requestId == 0) throw new ArgumentOutOfRangeException(nameof(requestId));
        if (body.Length > MaximumBodyBytes)
            throw new ArgumentOutOfRangeException(
                nameof(body), $"RPC body exceeds {MaximumBodyBytes} bytes.");
        var payload = new byte[checked(HeaderBytes + body.Length)];
        payload[0] = ProtocolVersion;
        payload[1] = (byte)kind;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), requestId);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(6), body.Length);
        body.CopyTo(payload.AsSpan(HeaderBytes));
        return payload;
    }

    internal static byte[] EncodeError(uint requestId, string error, int maximumBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (maximumBodyBytes is < 1 or > MaximumBodyBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBodyBytes));
        var buffer = new byte[maximumBodyBytes];
        var encoder = Utf8.GetEncoder();
        encoder.Convert(
            error.AsSpan(),
            buffer,
            flush: true,
            out _,
            out var bytesUsed,
            out _);
        return Encode(NetworkRpcMessageKind.Error, requestId, buffer.AsSpan(0, bytesUsed));
    }

    internal static string DecodeError(ReadOnlySpan<byte> body) => Utf8.GetString(body);

    internal static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out NetworkRpcMessage message,
        out string error)
    {
        message = default;
        error = string.Empty;
        if (payload.Length < HeaderBytes)
            return Fail("RPC message is shorter than its header.", out error);
        if (payload[0] != ProtocolVersion)
            return Fail($"RPC protocol {payload[0]} is unsupported.", out error);
        var kind = (NetworkRpcMessageKind)payload[1];
        if (!Enum.IsDefined(kind))
            return Fail($"RPC message kind {(byte)kind} is unknown.", out error);
        var requestId = BinaryPrimitives.ReadUInt32LittleEndian(payload[2..]);
        var bodyLength = BinaryPrimitives.ReadInt32LittleEndian(payload[6..]);
        if (requestId == 0) return Fail("RPC request id is zero.", out error);
        if (bodyLength is < 0 or > MaximumBodyBytes)
            return Fail("RPC body length is invalid.", out error);
        if (payload.Length != HeaderBytes + bodyLength)
            return Fail("RPC length does not match its header.", out error);
        message = new NetworkRpcMessage(kind, requestId, payload[HeaderBytes..].ToArray());
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
