using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TrustScore.Core.Audit;
using Xunit;

namespace TrustScore.Tests.Unit;

public class MerkleV2Tests
{
    private static byte[] Leaf(int i) => SHA256.HashData(Encoding.UTF8.GetBytes($"leaf-{i}"));

    private static MerkleTree Tree(MerkleTreeVersion version, int count)
    {
        var tree = new MerkleTree(version);
        for (var i = 0; i < count; i++) tree.AddLeafHash(Leaf(i));
        return tree;
    }

    // RFC 6962 §2.1, written from the RFC rather than from our code: MTH of n > 1 leaves splits at
    // k, the largest power of two smaller than n. (The leaves here are already hashed, so the
    // RFC's own leaf hashing does not apply.)
    private static byte[] Rfc6962Root(IReadOnlyList<byte[]> leaves)
    {
        if (leaves.Count == 1) return leaves[0];
        var k = 1;
        while (k * 2 < leaves.Count) k *= 2;
        var left = Rfc6962Root(leaves.Take(k).ToList());
        var right = Rfc6962Root(leaves.Skip(k).ToList());
        return SHA256.HashData(new byte[] { 0x01 }.Concat(left).Concat(right).ToArray());
    }

    [Fact]
    public void V2Root_MatchesRfc6962_ForEveryLeafCountUpTo40()
    {
        for (var n = 1; n <= 40; n++)
        {
            var leaves = Enumerable.Range(0, n).Select(Leaf).ToList();
            Tree(MerkleTreeVersion.V2, n).Root.Should().Equal(Rfc6962Root(leaves), $"leaf count {n}");
        }
    }

    [Fact]
    public void V2Proofs_VerifyForEveryLeaf_AndOnlyUnderV2()
    {
        for (var n = 1; n <= 40; n++)
        {
            var tree = Tree(MerkleTreeVersion.V2, n);
            for (var i = 0; i < n; i++)
            {
                var proof = tree.GetInclusionProof(i);
                MerkleTree.VerifyProof(Leaf(i), proof, tree.Root!, MerkleTreeVersion.V2)
                    .Should().BeTrue($"leaf {i} of {n}");
                if (n > 1)
                    MerkleTree.VerifyProof(Leaf(i), proof, tree.Root!, MerkleTreeVersion.V1)
                        .Should().BeFalse("a v2 proof is not a v1 proof");
            }
        }
    }

    [Fact]
    public void V2Proof_RejectsAnotherLeaf()
    {
        var tree = Tree(MerkleTreeVersion.V2, 7);
        MerkleTree.VerifyProof(Leaf(3), tree.GetInclusionProof(2), tree.Root!, MerkleTreeVersion.V2)
            .Should().BeFalse();
    }

    [Fact]
    public void V1_LetsTwoLeafListsShareARoot_V2DoesNot()
    {
        // CVE-2012-2459: duplicating the odd node means [a,b,c] and [a,b,c,c] hash identically,
        // so the root no longer pins down the leaf list.
        var abc = new[] { Leaf(0), Leaf(1), Leaf(2) };
        var abcc = new[] { Leaf(0), Leaf(1), Leaf(2), Leaf(2) };

        byte[] Root(MerkleTreeVersion v, byte[][] leaves)
        {
            var t = new MerkleTree(v);
            foreach (var l in leaves) t.AddLeafHash(l);
            return t.Root!;
        }

        Root(MerkleTreeVersion.V1, abc).Should().Equal(Root(MerkleTreeVersion.V1, abcc));
        Root(MerkleTreeVersion.V2, abc).Should().NotEqual(Root(MerkleTreeVersion.V2, abcc));
    }

    [Fact]
    public void V2Nodes_AreDomainSeparatedFromThePlainConcatenation()
    {
        var tree = Tree(MerkleTreeVersion.V2, 2);
        tree.Root.Should().NotEqual(SHA256.HashData(Leaf(0).Concat(Leaf(1)).ToArray()));
    }

    [Fact]
    public void V1Behaviour_IsUnchanged()
    {
        // Existing anchors must keep reproducing: pin v1 to the plain duplicate-last scheme.
        var tree = Tree(MerkleTreeVersion.V1, 3);
        var h01 = SHA256.HashData(Leaf(0).Concat(Leaf(1)).ToArray());
        var h22 = SHA256.HashData(Leaf(2).Concat(Leaf(2)).ToArray());
        tree.Root.Should().Equal(SHA256.HashData(h01.Concat(h22).ToArray()));
        new MerkleTree().Version.Should().Be(MerkleTreeVersion.V1);
    }
}

public class RatingLeafTests
{
    private static readonly RatingLeaf Sample = new(
        Id: Guid.Parse("3f2b8c1e-5d4a-4b7e-9c2f-0a1b2c3d4e5f"),
        ServiceDid: "api.example.com",
        CreatedAt: new DateTimeOffset(2026, 9, 24, 16, 2, 55, TimeSpan.Zero).AddTicks(1234560),
        LeafVersion: 2,
        StatusCode: 200,
        LatencyMs: 143,
        ResponseSizeBytes: 2048,
        SchemaValid: true,
        QualityScore: 4,
        HasReceipt: false,
        ReceiptVerified: false,
        SignatureVerified: true,
        Weight: 0.3);

    // Golden vector: the Node verifier in docs/ checks against the same values. Changing either
    // string is a protocol change, not a refactor.
    private const string SampleCanonical =
        "trustscore-leaf-v2\n3f2b8c1e-5d4a-4b7e-9c2f-0a1b2c3d4e5f\napi.example.com\n" +
        "2026-09-24T16:02:55.123456Z\n200\n143\n2048\ntrue\n4\nfalse\nfalse\ntrue\n0.300000";

    [Fact]
    public void CanonicalV2_IsExactlyTheSpecifiedString()
        => Sample.CanonicalV2().Should().Be(SampleCanonical);

    [Fact]
    public void V2Hash_IsSha256OfZeroPrefixedCanonical()
    {
        var expected = SHA256.HashData(new byte[] { 0x00 }.Concat(Encoding.UTF8.GetBytes(SampleCanonical)).ToArray());
        Sample.Hash().Should().Equal(expected);
    }

    [Fact]
    public void Nulls_AreEmptyLines()
    {
        var leaf = Sample with { ResponseSizeBytes = null, SchemaValid = null, QualityScore = null };
        leaf.CanonicalV2().Split('\n')[6..9].Should().Equal("", "", "");
    }

    [Fact]
    public void EveryCommittedField_ChangesTheHash()
    {
        var variants = new[]
        {
            Sample with { Id = Guid.NewGuid() },
            Sample with { ServiceDid = "api.example.org" },
            Sample with { CreatedAt = Sample.CreatedAt.AddTicks(10) },
            Sample with { StatusCode = 503 },
            Sample with { LatencyMs = 144 },
            Sample with { ResponseSizeBytes = null },
            Sample with { SchemaValid = false },
            Sample with { QualityScore = 5 },
            Sample with { HasReceipt = true },
            Sample with { ReceiptVerified = true },
            Sample with { SignatureVerified = false },
            Sample with { Weight = 0.15 },
        };

        variants.Select(v => v.HashHex()).Should().OnlyHaveUniqueItems()
            .And.NotContain(Sample.HashHex());
    }

    [Fact]
    public void Timestamp_IsUtcWhateverTheOffset()
        => (Sample with { CreatedAt = Sample.CreatedAt.ToOffset(TimeSpan.FromHours(2)) })
            .CanonicalV2().Should().Be(SampleCanonical);

    [Fact]
    public void Weight_IsCommittedAtSixDecimals()
    {
        (Sample with { Weight = 0.3 * 0.3405 }).CanonicalV2().Should().EndWith("\n0.102150");
        RatingLeaf.NormalizeWeight(0.1021500000001).Should().Be(0.10215);
    }

    [Fact]
    public void V1Leaf_IsTheLegacyHash()
    {
        var v1 = Sample with { LeafVersion = 1 };
        v1.Hash().Should().Equal(MerkleTree.ComputeLeafHash(v1.Id, v1.ServiceDid, v1.CreatedAt));
    }

    [Fact]
    public void V1AndV2Leaves_OfTheSameRating_Differ()
        => (Sample with { LeafVersion = 1 }).HashHex().Should().NotBe(Sample.HashHex());

    [Fact]
    public void NewlineInAField_IsRefused()
        => FluentActions.Invoking(() => (Sample with { ServiceDid = "a\nb" }).CanonicalV2())
            .Should().Throw<ArgumentException>();

    [Fact]
    public void UnknownVersion_IsRefused()
        => FluentActions.Invoking(() => (Sample with { LeafVersion = 3 }).Hash())
            .Should().Throw<InvalidOperationException>();
}
