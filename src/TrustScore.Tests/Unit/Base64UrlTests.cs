using FluentAssertions;
using TrustScore.Api.Receipts;
using Xunit;

namespace TrustScore.Tests.Unit;

public class Base64UrlTests
{
    [Theory]
    [InlineData("-_8", new byte[] { 0xFB, 0xFF })]         // url alphabet, unpadded
    [InlineData("-_8=", new byte[] { 0xFB, 0xFF })]        // padding tolerated
    [InlineData("AA", new byte[] { 0x00 })]
    [InlineData("AAA", new byte[] { 0x00, 0x00 })]
    [InlineData("AAAA", new byte[] { 0x00, 0x00, 0x00 })]
    [InlineData("", new byte[0])]
    public void Decode_AcceptsPaddedAndUnpadded(string input, byte[] expected)
        => Base64Url.Decode(input).Should().Equal(expected);

    [Theory]
    [InlineData("A")]       // a length of 1 mod 4 is never valid
    [InlineData("AA*A")]
    public void Decode_RejectsMalformedInput(string input)
        => FluentActions.Invoking(() => Base64Url.Decode(input)).Should().Throw<FormatException>();
}
