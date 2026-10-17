using System.Text;
using FluentAssertions;
using TrustScore.Api.Scoring;
using TrustScore.Core.Models;
using TrustScore.Core.X402;
using Xunit;

namespace TrustScore.Tests.Unit;

public class X402ChallengeTests
{
    // The shape the Bazaar catalog and live v2 servers use (an endpoint probed on 2026-10-17).
    internal const string V2Json =
        """
        {"x402Version":2,"error":"Payment required","resource":{"url":"https://api.example.com/x402/ping"},
         "accepts":[{"scheme":"exact","network":"eip155:8453","amount":"20000",
                     "asset":"0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913",
                     "payTo":"0x7284D41b5b852F2bd4c99bDf95043D84452d299C","maxTimeoutSeconds":300,
                     "extra":{"name":"USD Coin","version":"2"}}]}
        """;

    internal static string V2Header => Convert.ToBase64String(Encoding.UTF8.GetBytes(V2Json));

    private const string V1Body =
        """
        {"x402Version":1,"error":"X-PAYMENT header is required",
         "accepts":[{"scheme":"exact","network":"base","maxAmountRequired":"1000","resource":"https://api.example.com/v1",
                     "description":"","mimeType":"application/json","payTo":"0xabc","maxTimeoutSeconds":60,
                     "asset":"0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913"}]}
        """;

    [Fact]
    public void V2_IsReadFromThePaymentRequiredHeader()
    {
        var challenge = X402Challenge.Parse(V2Header, body: "{}");

        challenge.Should().NotBeNull();
        challenge!.Version.Should().Be(2);
        challenge.Accepts.Should().ContainSingle().Which.Should().Be(new X402Requirement(
            "exact", "eip155:8453", "20000", "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913",
            "0x7284D41b5b852F2bd4c99bDf95043D84452d299C"));
    }

    [Fact]
    public void V2_HeaderInTheUrlSafeAlphabetWithoutPadding_IsAccepted()
    {
        var urlSafe = V2Header.TrimEnd('=').Replace('+', '-').Replace('/', '_');
        X402Challenge.Parse(urlSafe, null).Should().NotBeNull();
    }

    [Fact]
    public void V1_IsReadFromTheBody_WithMaxAmountRequiredAsThePrice()
    {
        var challenge = X402Challenge.Parse(null, V1Body);

        challenge.Should().NotBeNull();
        challenge!.Version.Should().Be(1);
        challenge.Accepts.Single().Amount.Should().Be("1000");
    }

    [Fact]
    public void AnUnreadableHeader_IsNotRescuedByTheBody()
    {
        // The header is where a v2 server puts the challenge; if it is broken, the answer is broken.
        X402Challenge.Parse("not base64 !!", V1Body).Should().BeNull();
    }

    [Theory]
    [InlineData("""{"x402Version":3,"accepts":[{"scheme":"exact","network":"n","amount":"1","asset":"a","payTo":"p"}]}""")]
    [InlineData("""{"accepts":[{"scheme":"exact","network":"n","amount":"1","asset":"a","payTo":"p"}]}""")]
    [InlineData("""{"x402Version":2,"accepts":[]}""")]
    [InlineData("""{"x402Version":2,"accepts":[{"scheme":"exact","network":"n","amount":"1","asset":"a"}]}""")]
    [InlineData("""{"x402Version":2,"accepts":[{"scheme":"exact","network":"n","amount":"0.01","asset":"a","payTo":"p"}]}""")]
    [InlineData("""{"x402Version":2,"accepts":[{"scheme":"exact","network":"n","amount":1,"asset":"a","payTo":"p"}]}""")]
    [InlineData("""{"x402Version":2,"accepts":"exact"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""<html>Payment Required</html>""")]
    [InlineData("")]
    public void IncompleteOrForeignChallenges_AreInvalid(string body)
    {
        X402Challenge.Parse(null, body).Should().BeNull();
    }

    [Fact]
    public void OneCompleteRequirement_IsEnough_TheIncompleteOnesAreDropped()
    {
        var body = """
                   {"x402Version":2,"accepts":[{"scheme":"exact","network":"n"},
                    {"scheme":"exact","network":"solana:5ey","amount":"5","asset":"EPj","payTo":"9xQ"}]}
                   """;
        X402Challenge.Parse(null, body)!.Accepts.Should().ContainSingle().Which.Network.Should().Be("solana:5ey");
    }

    [Fact]
    public void AnOversizedBody_IsNotParsed()
    {
        var huge = V1Body.Replace("\"description\":\"\"", $"\"description\":\"{new string('x', 70_000)}\"");
        X402Challenge.Parse(null, huge).Should().BeNull();
    }
}

public class ResponseOutcomeTests
{
    [Theory]
    [InlineData(200, null, true)]
    [InlineData(204, false, true)]
    [InlineData(402, true, true)]
    [InlineData(402, false, false)]
    [InlineData(402, null, false)]
    [InlineData(404, true, false)]
    [InlineData(503, true, false)]
    public void Success_IsA2xx_OrA402WithAValidChallenge(int status, bool? schemaValid, bool expected)
    {
        ResponseOutcome.IsSuccess(status, schemaValid).Should().Be(expected);
    }

    [Fact]
    public void AValid402_RaisesAvailability_AnInvalidOneLeavesItNeutral()
    {
        var engine = new BetaReputationSystem();
        RatingDelta Delta(bool? schemaValid) => engine.ComputeDelta(new Rating
        {
            ServiceDid = "api.example.com/x402/ping",
            AgentDid = "did:web:trustscoreagent.com:probe",
            Metrics = new RatingMetrics { StatusCode = 402, LatencyMs = 300, SchemaValid = schemaValid },
            Weight = 0.3,
        });

        var valid = Delta(true);
        valid.AlphaAvailabilityDelta.Should().BeApproximately(0.3, 1e-9);
        valid.AlphaConformityDelta.Should().BeApproximately(0.3, 1e-9);

        var invalid = Delta(false);
        invalid.AlphaAvailabilityDelta.Should().Be(0);
        invalid.BetaAvailabilityDelta.Should().Be(0);
        invalid.BetaConformityDelta.Should().BeApproximately(0.3, 1e-9);
    }
}
