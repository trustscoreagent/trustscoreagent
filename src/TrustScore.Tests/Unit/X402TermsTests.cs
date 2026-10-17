using FluentAssertions;
using TrustScore.Core.X402;
using Xunit;

namespace TrustScore.Tests.Unit;

public class X402TermsTests
{
    private const string Usdc = "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913";
    private const string Payee = "0x7284D41b5b852F2bd4c99bDf95043D84452d299C";

    private static X402Requirement R(string amount = "10000", string payTo = Payee, string network = "eip155:8453",
        string asset = Usdc, string scheme = "exact") => new(scheme, network, amount, asset, payTo);

    [Fact]
    public void SameTerms_InAnotherOrderOrCase_AreNoChange()
    {
        var change = X402Terms.Compare(
            [R(), R(network: "solana", asset: "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v", payTo: "So1Payee")],
            [R(network: "solana", asset: "EPjFWdd5AufqSSqeM2qN1xzybapC8G4wEGGkZwyTDt1v", payTo: "So1Payee"),
             R(payTo: Payee.ToLowerInvariant(), asset: Usdc.ToUpperInvariant().Replace("0X", "0x"))]);

        change.Any.Should().BeFalse("EVM addresses are the same in any case");
    }

    [Fact]
    public void NewPayee_IsAPayeeChange_NotAPriceChange()
    {
        var change = X402Terms.Compare([R()], [R(payTo: "0x0000000000000000000000000000000000000bad")]);

        change.Should().Be(new X402TermsChange(PayTo: true, Price: false));
    }

    [Fact]
    public void NonEvmAddresses_AreCaseSensitive()
    {
        X402Terms.Compare([R(network: "solana", payTo: "AbC")], [R(network: "solana", payTo: "abc")])
            .PayTo.Should().BeTrue();
    }

    [Theory]
    [InlineData("20000", Usdc, "eip155:8453")]   // new amount
    [InlineData("10000", "0x0000000000000000000000000000000000000001", "eip155:8453")]  // new asset
    public void NewAmountOrAsset_IsAPriceChange(string amount, string asset, string network)
    {
        var change = X402Terms.Compare([R()], [R(amount: amount, asset: asset, network: network)]);

        change.Should().Be(new X402TermsChange(PayTo: false, Price: true));
    }

    [Fact]
    public void LeadingZerosInTheAmount_AreTheSamePrice()
    {
        X402Terms.Compare([R(amount: "10000")], [R(amount: "0010000")]).Price.Should().BeFalse();
    }

    [Fact]
    public void Canonical_DeduplicatesSortsAndCaps()
    {
        var many = Enumerable.Range(1, 40).Select(i => R(amount: i.ToString())).Append(R(amount: "1")).ToList();

        var canonical = X402Terms.Canonical(many);

        canonical.Should().HaveCount(X402Terms.MaxStoredRequirements);
        canonical.Should().OnlyHaveUniqueItems();
        X402Terms.Canonical([R(amount: "2"), R(amount: "1")]).Select(r => r.Amount).Should().Equal("1", "2");
    }
}
