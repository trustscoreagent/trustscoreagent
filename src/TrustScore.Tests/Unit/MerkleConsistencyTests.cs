using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TrustScore.Core.Audit;
using Xunit;

namespace TrustScore.Tests.Unit;

public class MerkleConsistencyTests
{
    private static byte[] Leaf(int i) => SHA256.HashData(Encoding.UTF8.GetBytes($"leaf-{i}"));

    private static List<byte[]> Leaves(int n) => Enumerable.Range(0, n).Select(Leaf).ToList();

    private static byte[] TreeRoot(IEnumerable<byte[]> leaves)
    {
        var tree = new MerkleTree(MerkleTreeVersion.V2);
        foreach (var l in leaves) tree.AddLeafHash(l);
        return tree.Root!;
    }

    [Fact]
    public void Root_IsTheV2TreeRoot()
    {
        for (var n = 1; n <= 40; n++)
            MerkleConsistency.Root(Leaves(n), 0, n).Should().Equal(TreeRoot(Leaves(n)), $"size {n}");
    }

    [Fact]
    public void EveryPrefix_IsProvablyConsistent_WithEveryLongerTree()
    {
        for (var n = 1; n <= 40; n++)
        {
            var all = Leaves(n);
            var newRoot = TreeRoot(all);
            for (var m = 1; m <= n; m++)
            {
                var proof = MerkleConsistency.Prove(all, m);
                MerkleConsistency.Verify(m, n, TreeRoot(all.Take(m)), newRoot, proof)
                    .Should().BeTrue($"{m} -> {n}");
            }
        }
    }

    // Golden vector computed independently with Python hashlib (RFC 6962 SUBPROOF), also pinned in
    // tools/verify-proof. Leaves are SHA256("leaf-0") .. SHA256("leaf-6").
    private static readonly string[] Proof3To7 =
    {
        "649837ddcb7e1967086d7d35aaef7b975c513815d96fc6e70015e93a2bfe0f9a",
        "9fde56c376760bd399b82eb8569229a2dff19219411ac71154dfeab2cf502454",
        "c76c1321b98ab0ea04447b38d8daeb85fa04df66731a8f25a60c84a1548d9831",
        "c28121395ace509462b8b9f255e9811949c00c347032fdf4004e53d1da650cbb",
    };

    [Fact]
    public void Proof3To7_MatchesTheIndependentlyComputedVector()
    {
        MerkleConsistency.Prove(Leaves(7), 3).Select(h => Convert.ToHexString(h).ToLowerInvariant())
            .Should().Equal(Proof3To7);
        MerkleConsistency.Verify(3, 7,
            Convert.FromHexString("3fd64e951bb292c4cc9ea78ea50e1115c0b754f0ca8b4a4f4a9610bd2c258877"),
            Convert.FromHexString("47249849653ade6eb70d84bfa744130e8ae6915a588a9488b95f67b7247756e3"),
            Proof3To7.Select(Convert.FromHexString).ToList()).Should().BeTrue();
    }

    [Fact]
    public void EqualSizes_NeedAnEmptyProof_AndEqualRoots()
    {
        var root = TreeRoot(Leaves(7));
        MerkleConsistency.Prove(Leaves(7), 7).Should().BeEmpty();
        MerkleConsistency.Verify(7, 7, root, root, new List<byte[]>()).Should().BeTrue();
        MerkleConsistency.Verify(7, 7, root, TreeRoot(Leaves(6)), new List<byte[]>()).Should().BeFalse();
    }

    [Fact]
    public void ATreeThatEditedAnAnchoredLeaf_CannotBeProvenConsistent()
    {
        // The case consistency exists for: history rewritten between two anchors. Whatever proof
        // the rewritten tree produces, it must not connect it to the root that was published.
        for (var n = 2; n <= 24; n++)
        {
            for (var m = 1; m < n; m++)
            {
                var original = Leaves(n);
                var oldRoot = TreeRoot(original.Take(m));
                var rewritten = original.ToList();
                rewritten[m / 2] = SHA256.HashData(Encoding.UTF8.GetBytes("edited"));

                var proof = MerkleConsistency.Prove(rewritten, m);
                MerkleConsistency.Verify(m, n, oldRoot, TreeRoot(rewritten), proof)
                    .Should().BeFalse($"edited leaf {m / 2}, {m} -> {n}");
            }
        }
    }

    [Fact]
    public void ATreeThatDroppedAnAnchoredLeaf_CannotBeProvenConsistent()
    {
        for (var n = 3; n <= 24; n++)
        {
            for (var m = 2; m < n; m++)
            {
                var original = Leaves(n);
                var oldRoot = TreeRoot(original.Take(m));
                var dropped = original.Where((_, i) => i != m - 1).ToList(); // an anchored leaf removed

                var proof = MerkleConsistency.Prove(dropped, m);
                MerkleConsistency.Verify(m, dropped.Count, oldRoot, TreeRoot(dropped), proof)
                    .Should().BeFalse($"dropped leaf {m - 1}, {m} -> {n - 1}");
            }
        }
    }

    [Fact]
    public void AnyAlteredProof_IsRejected()
    {
        var all = Leaves(23);
        var oldRoot = TreeRoot(all.Take(9));
        var newRoot = TreeRoot(all);
        var proof = MerkleConsistency.Prove(all, 9);
        proof.Should().NotBeEmpty();

        for (var i = 0; i < proof.Count; i++)
        {
            var altered = proof.ToList();
            altered[i] = SHA256.HashData(altered[i]);
            MerkleConsistency.Verify(9, 23, oldRoot, newRoot, altered).Should().BeFalse($"element {i}");
        }
        MerkleConsistency.Verify(9, 23, oldRoot, newRoot, proof.Take(proof.Count - 1).ToList()).Should().BeFalse();
        MerkleConsistency.Verify(9, 23, oldRoot, newRoot, proof.Append(Leaf(0)).ToList()).Should().BeFalse();
        MerkleConsistency.Verify(10, 23, oldRoot, newRoot, proof).Should().BeFalse("wrong old size");
    }

    [Fact]
    public void ImpossibleSizes_AreRejected()
    {
        var root = TreeRoot(Leaves(4));
        MerkleConsistency.Verify(0, 4, root, root, new List<byte[]>()).Should().BeFalse();
        MerkleConsistency.Verify(5, 4, root, root, new List<byte[]>()).Should().BeFalse();
        FluentActions.Invoking(() => MerkleConsistency.Prove(Leaves(4), 5)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => MerkleConsistency.Prove(Leaves(4), 0)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Extends_IsTheDirectPrefixCheck()
    {
        var all = Leaves(12);
        var oldRoot = TreeRoot(all.Take(5));

        MerkleConsistency.Extends(all, 5, oldRoot).Should().BeTrue();

        var edited = all.ToList();
        edited[2] = Leaf(99);
        MerkleConsistency.Extends(edited, 5, oldRoot).Should().BeFalse();
        MerkleConsistency.Extends(all.Take(4).ToList(), 5, oldRoot).Should().BeFalse("the new tree is smaller");
    }
}
