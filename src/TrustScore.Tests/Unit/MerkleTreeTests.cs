using FluentAssertions;
using TrustScore.Core.Audit;
using Xunit;

namespace TrustScore.Tests.Unit;

public class MerkleTreeTests
{
    [Fact]
    public void EmptyTree_HasNullRoot()
    {
        var tree = new MerkleTree();

        tree.Root.Should().BeNull();
        tree.RootHex.Should().BeNull();
        tree.LeafCount.Should().Be(0);
    }

    [Fact]
    public void SingleLeaf_RootIsLeafHash()
    {
        var tree = new MerkleTree();
        var id = Guid.NewGuid();
        var did = "did:web:test.example.com";
        var ts = DateTimeOffset.UtcNow;

        tree.AddLeaf(id, did, ts);

        tree.LeafCount.Should().Be(1);
        tree.Root.Should().NotBeNull();
        tree.RootHex.Should().NotBeNullOrEmpty();

        // Root should equal the leaf hash for a single leaf
        var expectedHash = MerkleTree.ComputeLeafHash(id, did, ts);
        tree.Root.Should().BeEquivalentTo(expectedHash);
    }

    [Fact]
    public void TwoLeaves_RootIsDifferentFromEither()
    {
        var tree = new MerkleTree();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var did = "did:web:test.example.com";
        var ts = DateTimeOffset.UtcNow;

        tree.AddLeaf(id1, did, ts);
        tree.AddLeaf(id2, did, ts);

        tree.LeafCount.Should().Be(2);

        var hash1 = MerkleTree.ComputeLeafHash(id1, did, ts);
        var hash2 = MerkleTree.ComputeLeafHash(id2, did, ts);

        tree.Root.Should().NotBeEquivalentTo(hash1);
        tree.Root.Should().NotBeEquivalentTo(hash2);
    }

    [Fact]
    public void DifferentData_ProducesDifferentRoots()
    {
        var tree1 = new MerkleTree();
        var tree2 = new MerkleTree();
        var ts = DateTimeOffset.UtcNow;

        tree1.AddLeaf(Guid.NewGuid(), "did:web:service-a.com", ts);
        tree2.AddLeaf(Guid.NewGuid(), "did:web:service-b.com", ts);

        tree1.RootHex.Should().NotBe(tree2.RootHex);
    }

    [Fact]
    public void SameData_ProducesSameRoot()
    {
        var id = Guid.Parse("12345678-1234-1234-1234-123456789012");
        var did = "did:web:test.example.com";
        var ts = DateTimeOffset.Parse("2026-04-03T14:00:00Z");

        var tree1 = new MerkleTree();
        var tree2 = new MerkleTree();

        tree1.AddLeaf(id, did, ts);
        tree2.AddLeaf(id, did, ts);

        tree1.RootHex.Should().Be(tree2.RootHex);
    }

    [Fact]
    public void InclusionProof_VerifiesCorrectly()
    {
        var tree = new MerkleTree();
        var ids = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();
        var did = "did:web:test.example.com";
        var ts = DateTimeOffset.UtcNow;

        foreach (var id in ids)
            tree.AddLeaf(id, did, ts);

        var root = tree.Root!;

        // Verify inclusion proof for each leaf
        for (int i = 0; i < ids.Count; i++)
        {
            var leafHash = MerkleTree.ComputeLeafHash(ids[i], did, ts);
            var proof = tree.GetInclusionProof(i);

            MerkleTree.VerifyProof(leafHash, proof, root).Should().BeTrue(
                $"Inclusion proof for leaf {i} should verify against root");
        }
    }

    [Fact]
    public void InclusionProof_FailsWithWrongLeaf()
    {
        var tree = new MerkleTree();
        var did = "did:web:test.example.com";
        var ts = DateTimeOffset.UtcNow;

        tree.AddLeaf(Guid.NewGuid(), did, ts);
        tree.AddLeaf(Guid.NewGuid(), did, ts);
        tree.AddLeaf(Guid.NewGuid(), did, ts);

        var root = tree.Root!;
        var proof = tree.GetInclusionProof(0);

        // Use a different leaf hash — should fail
        var fakeLeafHash = MerkleTree.ComputeLeafHash(Guid.NewGuid(), did, ts);
        MerkleTree.VerifyProof(fakeLeafHash, proof, root).Should().BeFalse();
    }

    [Fact]
    public void OddNumberOfLeaves_StillWorks()
    {
        var tree = new MerkleTree();
        var did = "did:web:test.example.com";
        var ts = DateTimeOffset.UtcNow;

        tree.AddLeaf(Guid.NewGuid(), did, ts);
        tree.AddLeaf(Guid.NewGuid(), did, ts);
        tree.AddLeaf(Guid.NewGuid(), did, ts);

        tree.Root.Should().NotBeNull();
        tree.LeafCount.Should().Be(3);
    }

    [Fact]
    public void LargeTree_ProofVerifies()
    {
        var tree = new MerkleTree();
        var did = "did:web:test.example.com";
        var ts = DateTimeOffset.UtcNow;
        var targetId = Guid.NewGuid();
        var targetIndex = 50;

        for (int i = 0; i < 100; i++)
        {
            var id = (i == targetIndex) ? targetId : Guid.NewGuid();
            tree.AddLeaf(id, did, ts);
        }

        var root = tree.Root!;
        var leafHash = MerkleTree.ComputeLeafHash(targetId, did, ts);
        var proof = tree.GetInclusionProof(targetIndex);

        MerkleTree.VerifyProof(leafHash, proof, root).Should().BeTrue();
    }
}
