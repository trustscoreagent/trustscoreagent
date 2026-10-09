using System.Security.Cryptography;
using System.Text;

namespace TrustScore.Core.Audit;

/// <summary>
/// The OpenTimestamps proof format (https://opentimestamps.org), enough of it to stamp anchored
/// Merkle roots, merge several calendars' answers, upgrade pending attestations into Bitcoin ones,
/// and write the <c>.ots</c> file that the standard <c>ots</c> client verifies.
///
/// A timestamp is a tree: from a message, each branch applies operations (append, prepend, hash)
/// and ends in an attestation, "this message is committed in Bitcoin block N" or "calendar X will
/// get it into Bitcoin". Serialization: each node is a list of items; every item but the last is
/// prefixed with 0xff; an attestation is 0x00, an 8-byte tag and a length-prefixed payload; an
/// operation is its tag, its argument if binary, then the child node.
/// </summary>
public sealed class OtsTimestamp
{
    public const int MaxProofBytes = 64 * 1024;
    private const int MaxDepth = 256;
    private const int MaxOpArgument = 4096;
    private const int MaxPayload = 8192;

    private static readonly byte[] FileMagic =
        [0x00, .. "OpenTimestamps"u8, 0x00, 0x00, .. "Proof"u8, 0x00, 0xbf, 0x89, 0xe2, 0xe8, 0x84, 0xe8, 0x92, 0x94];
    private const byte FileFormatVersion = 1;

    public List<OtsItem> Items { get; } = [];

    /// <summary>Parses a serialized timestamp (as a calendar returns it, without the file header).</summary>
    public static OtsTimestamp Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxProofBytes)
            throw new FormatException("Timestamp too large.");
        var reader = new Reader(bytes);
        var timestamp = ReadNode(ref reader, 0);
        if (!reader.AtEnd)
            throw new FormatException("Trailing bytes after the timestamp.");
        return timestamp;
    }

    /// <summary>Merges timestamps of the same message: their branches become siblings.</summary>
    public static OtsTimestamp Merge(IEnumerable<OtsTimestamp> timestamps)
    {
        var merged = new OtsTimestamp();
        foreach (var t in timestamps)
            merged.Items.AddRange(t.Items);
        if (merged.Items.Count == 0)
            throw new ArgumentException("Nothing to merge.", nameof(timestamps));
        return merged;
    }

    public byte[] Serialize()
    {
        using var stream = new MemoryStream();
        WriteNode(stream, this);
        return stream.ToArray();
    }

    /// <summary>
    /// The detached <c>.ots</c> file for a SHA-256 digest: <c>ots verify -d &lt;digest hex&gt; file.ots</c>
    /// checks it. Here the digest is the anchored Merkle root itself.
    /// </summary>
    public byte[] ToDetachedFile(ReadOnlySpan<byte> sha256Digest)
    {
        if (sha256Digest.Length != 32)
            throw new ArgumentException("A SHA-256 digest is 32 bytes.", nameof(sha256Digest));
        using var stream = new MemoryStream();
        stream.Write(FileMagic);
        stream.WriteByte(FileFormatVersion);
        stream.WriteByte(OtsOp.Sha256);
        stream.Write(sha256Digest);
        WriteNode(stream, this);
        return stream.ToArray();
    }

    /// <summary>Every attestation with the message it commits to, starting from <paramref name="message"/>.</summary>
    public IEnumerable<(OtsAttestation Attestation, byte[] Message)> Attestations(byte[] message)
    {
        foreach (var item in Items)
        {
            switch (item)
            {
                case OtsAttestation attestation:
                    yield return (attestation, message);
                    break;
                case OtsOp op:
                    foreach (var inner in op.Child.Attestations(op.Apply(message)))
                        yield return inner;
                    break;
            }
        }
    }

    /// <summary>
    /// Replaces each pending attestation for which <paramref name="upgrade"/> returns a timestamp of
    /// its message by that timestamp's branches. Returns how many were replaced.
    /// </summary>
    public int ReplacePending(byte[] message, Func<OtsAttestation, byte[], OtsTimestamp?> upgrade)
    {
        var replaced = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            switch (Items[i])
            {
                case OtsAttestation { IsPending: true } pending when upgrade(pending, message) is { } upgraded:
                    Items.RemoveAt(i);
                    Items.InsertRange(i, upgraded.Items);
                    i += upgraded.Items.Count - 1;
                    replaced++;
                    break;
                case OtsOp op:
                    replaced += op.Child.ReplacePending(op.Apply(message), upgrade);
                    break;
            }
        }
        return replaced;
    }

    private static OtsTimestamp ReadNode(ref Reader reader, int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException("Timestamp nested too deeply.");
        var node = new OtsTimestamp();
        var tag = reader.ReadByte();
        while (tag == 0xff)
        {
            node.Items.Add(ReadItem(ref reader, reader.ReadByte(), depth));
            tag = reader.ReadByte();
        }
        node.Items.Add(ReadItem(ref reader, tag, depth));
        return node;
    }

    private static OtsItem ReadItem(ref Reader reader, byte tag, int depth)
    {
        if (tag == 0x00)
        {
            var attestationTag = reader.ReadBytes(8).ToArray();
            var payload = reader.ReadVarBytes(MaxPayload).ToArray();
            return new OtsAttestation(attestationTag, payload);
        }

        byte[]? argument = null;
        if (OtsOp.IsBinary(tag))
        {
            argument = reader.ReadVarBytes(MaxOpArgument).ToArray();
            if (argument.Length == 0)
                throw new FormatException("Empty operation argument.");
        }
        else if (!OtsOp.IsUnary(tag))
        {
            throw new FormatException($"Unknown operation 0x{tag:x2}.");
        }
        return new OtsOp(tag, argument, ReadNode(ref reader, depth + 1));
    }

    private static void WriteNode(Stream stream, OtsTimestamp node)
    {
        if (node.Items.Count == 0)
            throw new InvalidOperationException("A timestamp node needs at least one item.");
        for (var i = 0; i < node.Items.Count; i++)
        {
            if (i < node.Items.Count - 1)
                stream.WriteByte(0xff);
            switch (node.Items[i])
            {
                case OtsAttestation a:
                    stream.WriteByte(0x00);
                    stream.Write(a.Tag);
                    WriteVarBytes(stream, a.Payload);
                    break;
                case OtsOp op:
                    stream.WriteByte(op.Tag);
                    if (op.Argument is not null)
                        WriteVarBytes(stream, op.Argument);
                    WriteNode(stream, op.Child);
                    break;
            }
        }
    }

    internal static void WriteVarUInt(Stream stream, ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7f);
            value >>= 7;
            stream.WriteByte(value == 0 ? b : (byte)(b | 0x80));
        } while (value != 0);
    }

    internal static void WriteVarBytes(Stream stream, byte[] bytes)
    {
        WriteVarUInt(stream, (ulong)bytes.Length);
        stream.Write(bytes);
    }

    internal ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _position;

        public readonly bool AtEnd => _position == _bytes.Length;

        public byte ReadByte()
        {
            if (_position >= _bytes.Length)
                throw new FormatException("Truncated timestamp.");
            return _bytes[_position++];
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (count < 0 || _position + count > _bytes.Length)
                throw new FormatException("Truncated timestamp.");
            var slice = _bytes.Slice(_position, count);
            _position += count;
            return slice;
        }

        public ulong ReadVarUInt()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = ReadByte();
                value |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0)
                    return value;
            }
            throw new FormatException("Variable-length integer too long.");
        }

        public ReadOnlySpan<byte> ReadVarBytes(int max)
        {
            var length = ReadVarUInt();
            if (length > (ulong)max)
                throw new FormatException("Field too long.");
            return ReadBytes((int)length);
        }
    }
}

public abstract record OtsItem;

/// <summary>An operation applied to the message on the way to the attestations below it.</summary>
public sealed record OtsOp(byte Tag, byte[]? Argument, OtsTimestamp Child) : OtsItem
{
    public const byte Sha1 = 0x02;
    public const byte Ripemd160 = 0x03;
    public const byte Sha256 = 0x08;
    public const byte Keccak256 = 0x67;
    public const byte Append = 0xf0;
    public const byte Prepend = 0xf1;
    public const byte Reverse = 0xf2;
    public const byte Hexlify = 0xf3;

    public static bool IsBinary(byte tag) => tag is Append or Prepend;
    public static bool IsUnary(byte tag) => tag is Sha1 or Ripemd160 or Sha256 or Keccak256 or Reverse or Hexlify;

    public byte[] Apply(byte[] message) => Tag switch
    {
        Append => [.. message, .. Argument!],
        Prepend => [.. Argument!, .. message],
        Sha256 => SHA256.HashData(message),
        Sha1 => SHA1.HashData(message),
        Reverse => message.Reverse().ToArray(),
        Hexlify => Encoding.ASCII.GetBytes(Convert.ToHexString(message).ToLowerInvariant()),
        // Not used by any calendar; a branch through these is kept but cannot be followed here.
        _ => throw new NotSupportedException($"Operation 0x{Tag:x2} is not supported."),
    };
}

/// <summary>Where a branch ends: a Bitcoin block, a calendar's promise, or a kind this code does not know.</summary>
public sealed record OtsAttestation(byte[] Tag, byte[] Payload) : OtsItem
{
    public static readonly byte[] PendingTag = [0x83, 0xdf, 0xe3, 0x0d, 0x2e, 0xf9, 0x0c, 0x8e];
    public static readonly byte[] BitcoinTag = [0x05, 0x88, 0x96, 0x0d, 0x73, 0xd7, 0x19, 0x01];

    public bool IsPending => Tag.AsSpan().SequenceEqual(PendingTag);
    public bool IsBitcoin => Tag.AsSpan().SequenceEqual(BitcoinTag);

    /// <summary>The calendar URL of a pending attestation (payload = length-prefixed URL).</summary>
    public string? CalendarUrl
    {
        get
        {
            if (!IsPending) return null;
            var reader = new OtsTimestamp.Reader(Payload);
            return Encoding.UTF8.GetString(reader.ReadVarBytes(1000));
        }
    }

    /// <summary>The block height of a Bitcoin attestation (payload = varuint height).</summary>
    public long? BitcoinHeight
    {
        get
        {
            if (!IsBitcoin) return null;
            var reader = new OtsTimestamp.Reader(Payload);
            return (long)reader.ReadVarUInt();
        }
    }

    public static OtsAttestation Pending(string calendarUrl)
    {
        using var payload = new MemoryStream();
        OtsTimestamp.WriteVarBytes(payload, Encoding.UTF8.GetBytes(calendarUrl));
        return new OtsAttestation(PendingTag, payload.ToArray());
    }

    public static OtsAttestation Bitcoin(long height)
    {
        using var payload = new MemoryStream();
        OtsTimestamp.WriteVarUInt(payload, (ulong)height);
        return new OtsAttestation(BitcoinTag, payload.ToArray());
    }
}
