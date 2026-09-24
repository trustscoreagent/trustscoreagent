using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TrustScore.Api.Jobs;
using TrustScore.Core.Audit;
using Xunit;

namespace TrustScore.Tests.Unit;

/// <summary>The anchoring job's own append-only check against the previous v2 anchor.</summary>
public class AnchorConsistencyCheckTests
{
    private static byte[] Leaf(int i) => SHA256.HashData(Encoding.UTF8.GetBytes($"leaf-{i}"));

    private static List<byte[]> Leaves(int n) => Enumerable.Range(0, n).Select(Leaf).ToList();

    private static byte[] TreeRoot(IEnumerable<byte[]> leaves)
    {
        var tree = new MerkleTree(MerkleTreeVersion.V2);
        foreach (var l in leaves) tree.AddLeafHash(l);
        return tree.Root!;
    }

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void Job_AcceptsAnAnchorThatOnlyAppended()
        => HourlyJob.CheckExtendsPrevious(Leaves(12), 7, Hex(TreeRoot(Leaves(7))))
            .Should().Be(HourlyJob.AnchorConsistency.Consistent);

    [Fact]
    public void Job_FlagsAnAnchoredRatingThatDisappeared()
    {
        var withoutOne = Leaves(12).Where((_, i) => i != 3).ToList();
        HourlyJob.CheckExtendsPrevious(withoutOne, 7, Hex(TreeRoot(Leaves(7))))
            .Should().Be(HourlyJob.AnchorConsistency.Inconsistent);
    }

    [Fact]
    public void Job_FlagsALogThatShrank()
        => HourlyJob.CheckExtendsPrevious(Leaves(5), 7, Hex(TreeRoot(Leaves(7))))
            .Should().Be(HourlyJob.AnchorConsistency.Shrunk);

    [Fact]
    public void Job_HasNothingToCompare_BeforeTheFirstV2Anchor()
        => HourlyJob.CheckExtendsPrevious(Leaves(5), null, null)
            .Should().Be(HourlyJob.AnchorConsistency.NoPreviousV2);
}
