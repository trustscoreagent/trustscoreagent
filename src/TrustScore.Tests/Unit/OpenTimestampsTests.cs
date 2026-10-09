using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TrustScore.Api.Jobs;
using TrustScore.Core.Audit;
using TrustScore.Tests.TestSupport;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>
/// The OpenTimestamps format against real calendar answers and the reference implementation's
/// output (see <see cref="OtsVectors"/>).
/// </summary>
public class OpenTimestampsTests
{
    private static readonly byte[] Digest = OtsVectors.Bytes(OtsVectors.Digest);

    private static readonly string[] Answers = [OtsVectors.AlicePool, OtsVectors.BobPool, OtsVectors.EternityWallPool];

    private static Dictionary<string, string> PendingMessagesOf(OtsTimestamp timestamp) =>
        timestamp.Attestations(Digest).ToDictionary(
            a => a.Attestation.CalendarUrl!, a => Convert.ToHexString(a.Message).ToLowerInvariant());

    [Theory]
    [InlineData(OtsVectors.AlicePool)]
    [InlineData(OtsVectors.BobPool)]
    [InlineData(OtsVectors.EternityWallPool)]
    public void CalendarAnswer_RoundTripsByteForByte(string hex)
        => OtsTimestamp.Parse(OtsVectors.Bytes(hex)).Serialize().Should().Equal(OtsVectors.Bytes(hex));

    [Fact]
    public void PendingAttestations_CommitToTheSameMessagesAsTheReferenceImplementation()
    {
        var merged = OtsTimestamp.Merge(Answers.Select(h => OtsTimestamp.Parse(OtsVectors.Bytes(h))));

        PendingMessagesOf(merged).Should().BeEquivalentTo(OtsVectors.PendingMessages);
    }

    [Fact]
    public void DetachedFile_HasTheStandardHeader_AndTheSameProofAsTheReferenceFile()
    {
        var merged = OtsTimestamp.Merge(Answers.Select(h => OtsTimestamp.Parse(OtsVectors.Bytes(h))));
        var file = merged.ToDetachedFile(Digest);
        var reference = OtsVectors.Bytes(OtsVectors.ReferenceFile);

        // Magic, version 1, SHA-256 file hash op and digest: 31 + 1 + 1 + 32 bytes, as in the reference.
        const int headerLength = 31 + 1 + 1 + 32;
        file.AsSpan(0, headerLength).ToArray().Should().Equal(reference.AsSpan(0, headerLength).ToArray());

        // The reference sorts branches, so compare what they prove rather than their order.
        var ours = OtsTimestamp.Parse(file.AsSpan(headerLength));
        var theirs = OtsTimestamp.Parse(reference.AsSpan(headerLength));
        PendingMessagesOf(ours).Should().BeEquivalentTo(PendingMessagesOf(theirs));
        file.Length.Should().Be(reference.Length);
    }

    [Fact]
    public void ReplacePending_SplicesInTheBitcoinProof_AndKeepsTheOtherBranches()
    {
        var merged = OtsTimestamp.Merge(Answers.Select(h => OtsTimestamp.Parse(OtsVectors.Bytes(h))));
        var upgrade = new OtsTimestamp();
        upgrade.Items.Add(new OtsOp(OtsOp.Sha256, null, Leaf(OtsAttestation.Bitcoin(917_342))));

        var replaced = merged.ReplacePending(Digest, (attestation, _) =>
            attestation.CalendarUrl == "https://alice.btc.calendar.opentimestamps.org" ? upgrade : null);

        replaced.Should().Be(1);
        var reparsed = OtsTimestamp.Parse(merged.Serialize());
        var attestations = reparsed.Attestations(Digest).Select(a => a.Attestation).ToList();
        attestations.Select(a => a.BitcoinHeight).OfType<long>().Should().Equal(917_342);
        attestations.Count(a => a.IsPending).Should().Be(2);

        // The Bitcoin attestation commits to SHA-256 of alice's pending message.
        var bitcoin = reparsed.Attestations(Digest).Single(a => a.Attestation.IsBitcoin);
        bitcoin.Message.Should().Equal(System.Security.Cryptography.SHA256.HashData(
            OtsVectors.Bytes(OtsVectors.PendingMessages["https://alice.btc.calendar.opentimestamps.org"])));
    }

    [Fact]
    public void Attestation_Payloads_Decode()
    {
        OtsAttestation.Pending("https://x.calendar.example").CalendarUrl.Should().Be("https://x.calendar.example");
        OtsAttestation.Bitcoin(0).BitcoinHeight.Should().Be(0);
        OtsAttestation.Bitcoin(917_342).BitcoinHeight.Should().Be(917_342);
        OtsAttestation.Bitcoin(917_342).CalendarUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("")]                                   // empty
    [InlineData("f008")]                               // truncated argument
    [InlineData("08")]                                 // operation without a child
    [InlineData("4200")]                               // unknown operation
    [InlineData("f00001")]                             // empty append argument
    [InlineData("0083dfe30d2ef90c8e0000")]             // trailing byte
    [InlineData("ffff0083dfe30d2ef90c8e00")]           // fork marker where an item belongs
    public void Malformed_IsRejected(string hex)
    {
        var act = () => OtsTimestamp.Parse(Convert.FromHexString(hex));
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void DeepNesting_IsRejected()
    {
        // 300 nested SHA-256 operations, then an attestation.
        var bytes = Enumerable.Repeat(OtsOp.Sha256, 300).Concat(OtsAttestation.Bitcoin(1).Tag.Prepend((byte)0)).Concat(new byte[] { 1, 1 }).ToArray();
        var act = () => OtsTimestamp.Parse(bytes);
        act.Should().Throw<FormatException>().WithMessage("*deeply*");
    }

    // The upgrade only ever calls a known calendar, whatever URL a stored proof contains.
    [Theory]
    [InlineData("https://alice.btc.calendar.opentimestamps.org", true)]
    [InlineData("https://finney.calendar.eternitywall.com", true)]
    [InlineData("https://btc.calendar.catallaxy.com", true)]
    [InlineData("http://alice.btc.calendar.opentimestamps.org", false)]
    [InlineData("https://alice.btc.calendar.opentimestamps.org:8443", false)]
    [InlineData("https://alice.btc.calendar.opentimestamps.org/x", false)]
    [InlineData("https://user@alice.btc.calendar.opentimestamps.org", false)]
    [InlineData("https://calendar.opentimestamps.org.evil.example", false)]
    [InlineData("https://evilcalendar.opentimestamps.org", false)]
    [InlineData("not a url", false)]
    public void UpgradeCalendars_AreAllowListed(string url, bool allowed) =>
        new AnchorTimestamper(null!, null!, Options.Create(new OpenTimestampsOptions()),
                NullLogger<AnchorTimestamper>.Instance)
            .IsUpgradeCalendar(url).Should().Be(allowed);

    private static OtsTimestamp Leaf(OtsAttestation attestation)
    {
        var node = new OtsTimestamp();
        node.Items.Add(attestation);
        return node;
    }
}
