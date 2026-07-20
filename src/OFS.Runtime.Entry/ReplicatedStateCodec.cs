using System.Buffers.Binary;

namespace OFS.Runtime.Entry;

internal enum ReplicatedStateMessageKind : byte
{
    SyncRequest = 1,
    Snapshot = 2,
}

internal readonly record struct ReplicatedStateMessage(
    ReplicatedStateMessageKind Kind,
    ulong Revision,
    byte[] Value);

internal static class ReplicatedStateCodec
{
    internal const byte ProtocolVersion = 1;
    internal const int SnapshotHeaderBytes = 14;
    internal const int MaximumValueBytes =
        NetworkEnvelopeCodec.MaxPayloadBytes - SnapshotHeaderBytes;

    internal static byte[] EncodeSyncRequest() =>
        [ProtocolVersion, (byte)ReplicatedStateMessageKind.SyncRequest];

    internal static byte[] EncodeSnapshot(ulong revision, ReadOnlySpan<byte> value)
    {
        if (revision == 0)
            throw new ArgumentOutOfRangeException(nameof(revision), "Revision zero is not authoritative.");
        if (value.Length > MaximumValueBytes)
            throw new ArgumentOutOfRangeException(
                nameof(value), $"Replicated value exceeds {MaximumValueBytes} bytes.");
        var message = new byte[checked(SnapshotHeaderBytes + value.Length)];
        message[0] = ProtocolVersion;
        message[1] = (byte)ReplicatedStateMessageKind.Snapshot;
        BinaryPrimitives.WriteUInt64LittleEndian(message.AsSpan(2), revision);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(10), value.Length);
        value.CopyTo(message.AsSpan(SnapshotHeaderBytes));
        return message;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out ReplicatedStateMessage message,
        out string error)
    {
        message = default;
        error = string.Empty;
        if (payload.Length < 2) return Fail("State message is shorter than its header.", out error);
        if (payload[0] != ProtocolVersion)
            return Fail($"State protocol {payload[0]} is unsupported.", out error);
        var kind = (ReplicatedStateMessageKind)payload[1];
        if (kind == ReplicatedStateMessageKind.SyncRequest)
        {
            if (payload.Length != 2)
                return Fail("Sync request contains trailing data.", out error);
            message = new ReplicatedStateMessage(kind, 0, []);
            return true;
        }
        if (kind != ReplicatedStateMessageKind.Snapshot)
            return Fail($"State message kind {(byte)kind} is unknown.", out error);
        if (payload.Length < SnapshotHeaderBytes)
            return Fail("Snapshot is shorter than its header.", out error);
        var revision = BinaryPrimitives.ReadUInt64LittleEndian(payload[2..]);
        var valueLength = BinaryPrimitives.ReadInt32LittleEndian(payload[10..]);
        if (revision == 0) return Fail("Snapshot revision is zero.", out error);
        if (valueLength is < 0 or > MaximumValueBytes)
            return Fail("Snapshot value length is invalid.", out error);
        if (payload.Length != SnapshotHeaderBytes + valueLength)
            return Fail("Snapshot length does not match its header.", out error);
        message = new ReplicatedStateMessage(
            kind,
            revision,
            payload[SnapshotHeaderBytes..].ToArray());
        return true;
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
