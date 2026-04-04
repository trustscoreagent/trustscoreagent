using FluentAssertions;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Unit;

public class ServiceIdentifierTests
{
    [Theory]
    [InlineData("did:web:api.example.com", "api.example.com")]
    [InlineData("did:web:API.Example.COM", "api.example.com")]
    [InlineData("https://api.example.com", "api.example.com")]
    [InlineData("https://api.example.com/v1/translate", "api.example.com")]
    [InlineData("https://api.example.com:8080/path", "api.example.com")]
    [InlineData("http://api.example.com", "api.example.com")]
    [InlineData("api.example.com", "api.example.com")]
    [InlineData("API.Example.COM", "api.example.com")]
    [InlineData("api.example.com:8080", "api.example.com")]
    [InlineData("api.example.com/v1/foo", "api.example.com")]
    [InlineData("  api.example.com  ", "api.example.com")]
    public void Normalize_VariousFormats_ReturnsCanonicalDomain(string input, string expected)
    {
        ServiceIdentifier.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Normalize_SameServiceDifferentFormats_ReturnsSameResult()
    {
        var url = ServiceIdentifier.Normalize("https://api.example.com/v1/translate");
        var domain = ServiceIdentifier.Normalize("api.example.com");
        var did = ServiceIdentifier.Normalize("did:web:api.example.com");

        url.Should().Be(domain);
        domain.Should().Be(did);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Normalize_EmptyOrNull_Throws(string? input)
    {
        var act = () => ServiceIdentifier.Normalize(input!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToDid_ConvertsCanonicalToDid()
    {
        ServiceIdentifier.ToDid("api.example.com").Should().Be("did:web:api.example.com");
    }
}
