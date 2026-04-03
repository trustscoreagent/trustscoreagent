using FluentAssertions;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Unit;

public class ReceiptPayloadTests
{
    [Fact]
    public void VerificationResult_Verified_HasWeight1()
    {
        var payload = new ReceiptPayload
        {
            ServiceDid = "did:web:test.example.com",
            AgentDid = "did:web:agent.example.com",
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            Nonce = Guid.NewGuid().ToString(),
            Endpoint = "/test",
            Method = "POST",
            StatusCode = 200,
        };

        var result = ReceiptVerificationResult.Verified(payload);

        result.IsVerified.Should().BeTrue();
        result.Weight.Should().Be(1.0);
        result.Payload.Should().NotBeNull();
        result.Status.Should().Be(ReceiptVerificationStatus.Valid);
    }

    [Fact]
    public void VerificationResult_Failed_HasWeight03()
    {
        var result = ReceiptVerificationResult.Failed(ReceiptVerificationStatus.InvalidSignature);

        result.IsVerified.Should().BeFalse();
        result.Weight.Should().Be(0.3);
        result.Payload.Should().BeNull();
    }

    [Fact]
    public void VerificationResult_Rejected_HasWeight0()
    {
        var result = ReceiptVerificationResult.Rejected(ReceiptVerificationStatus.NonceAlreadyUsed);

        result.IsVerified.Should().BeFalse();
        result.Weight.Should().Be(0.0);
    }

    [Fact]
    public void ReceiptPayload_OptionalFieldsAreNullable()
    {
        var payload = new ReceiptPayload
        {
            ServiceDid = "did:web:test.example.com",
            AgentDid = "did:web:agent.example.com",
            Timestamp = DateTimeOffset.UtcNow.ToString("o"),
            Nonce = Guid.NewGuid().ToString(),
            Endpoint = "/api/test",
            Method = "GET",
            StatusCode = 200,
            // response_hash and latency_ms are optional
        };

        payload.ResponseHash.Should().BeNull();
        payload.LatencyMs.Should().BeNull();
        payload.Version.Should().Be("1.0");
    }
}
