using FluentAssertions;
using TrustScore.Core.Models;
using Xunit;

namespace TrustScore.Tests.Unit;

public class ServiceIdentifierTests
{
    // --- Normalize: endpoint level (with path) ---

    [Theory]
    [InlineData("https://api.example.com/v1/translate", "api.example.com/v1/translate")]
    [InlineData("https://api.example.com/v1/translate?key=123", "api.example.com/v1/translate")]
    [InlineData("https://api.example.com/v1/translate#section", "api.example.com/v1/translate")]
    [InlineData("http://api.example.com/v1/translate", "api.example.com/v1/translate")]
    [InlineData("https://API.Example.COM/V1/Translate", "api.example.com/v1/translate")]
    [InlineData("api.example.com/v1/translate", "api.example.com/v1/translate")]
    [InlineData("api.example.com:8080/v1/translate", "api.example.com/v1/translate")]
    public void Normalize_WithPath_KeepsPath(string input, string expected)
    {
        ServiceIdentifier.Normalize(input).Should().Be(expected);
    }

    // --- Normalize: provider level (domain only) ---

    [Theory]
    [InlineData("https://api.example.com", "api.example.com")]
    [InlineData("https://api.example.com/", "api.example.com")]
    [InlineData("api.example.com", "api.example.com")]
    [InlineData("API.Example.COM", "api.example.com")]
    [InlineData("api.example.com:8080", "api.example.com")]
    [InlineData("did:web:api.example.com", "api.example.com")]
    [InlineData("  api.example.com  ", "api.example.com")]
    public void Normalize_DomainOnly_ReturnsProvider(string input, string expected)
    {
        ServiceIdentifier.Normalize(input).Should().Be(expected);
    }

    // --- Same provider, different endpoints ---

    [Fact]
    public void Normalize_SameProviderDifferentPaths_AreDifferent()
    {
        var translate = ServiceIdentifier.Normalize("https://api.example.com/v1/translate");
        var summarize = ServiceIdentifier.Normalize("https://api.example.com/v1/summarize");

        translate.Should().NotBe(summarize);
    }

    [Fact]
    public void Normalize_SameEndpointDifferentFormats_AreSame()
    {
        var url = ServiceIdentifier.Normalize("https://api.example.com/v1/translate");
        var raw = ServiceIdentifier.Normalize("api.example.com/v1/translate");

        url.Should().Be(raw);
    }

    // --- ExtractProvider ---

    [Theory]
    [InlineData("api.example.com/v1/translate", "api.example.com")]
    [InlineData("api.example.com/v1/summarize", "api.example.com")]
    [InlineData("api.example.com", "api.example.com")]
    public void ExtractProvider_ReturnsDoaminOnly(string input, string expected)
    {
        ServiceIdentifier.ExtractProvider(input).Should().Be(expected);
    }

    [Fact]
    public void ExtractProvider_SameForAllEndpoints()
    {
        var p1 = ServiceIdentifier.ExtractProvider("api.example.com/v1/translate");
        var p2 = ServiceIdentifier.ExtractProvider("api.example.com/v1/summarize");
        var p3 = ServiceIdentifier.ExtractProvider("api.example.com/v2/analyze");

        p1.Should().Be(p2).And.Be(p3).And.Be("api.example.com");
    }

    // --- IsProviderLevel ---

    [Theory]
    [InlineData("api.example.com", true)]
    [InlineData("api.example.com/v1/translate", false)]
    public void IsProviderLevel_CorrectlyIdentifies(string input, bool expected)
    {
        ServiceIdentifier.IsProviderLevel(input).Should().Be(expected);
    }

    // --- ToDid ---

    [Fact]
    public void ToDid_UsesProviderOnly()
    {
        ServiceIdentifier.ToDid("api.example.com/v1/translate").Should().Be("did:web:api.example.com");
        ServiceIdentifier.ToDid("api.example.com").Should().Be("did:web:api.example.com");
    }

    // --- Validation ---

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
    public void Normalize_TooLong_Throws()
    {
        var longInput = new string('a', 501);
        var act = () => ServiceIdentifier.Normalize(longInput);
        act.Should().Throw<ArgumentException>();
    }

    // --- PublicHost: only services others can reach may be rated ---

    [Theory]
    [InlineData("http://127.0.0.1/control")]
    [InlineData("127.0.0.1/t/f007112/1/control")]
    [InlineData("http://localhost:3000/api")]
    [InlineData("localhost")]
    [InlineData("10.1.2.3/x")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255/x")]
    [InlineData("192.168.1.10:8080/x")]
    [InlineData("169.254.169.254/latest/meta-data")]
    [InlineData("0.0.0.0")]
    [InlineData("http://[::1]:8080/x")]
    [InlineData("http://[fd00::1]/x")]
    [InlineData("printer.local")]
    [InlineData("api.corp.internal/v1")]
    [InlineData("app.localhost")]
    [InlineData("nas.home.arpa")]
    [InlineData("intranet/x")]
    public void PublicHost_RejectsHostsOnlyTheRaterCanReach(string input)
    {
        PublicHost.IsPublic(ServiceIdentifier.Normalize(input)).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://api.example.com/v1/translate")]
    [InlineData("did:web:api.example.com")]
    [InlineData("8.8.8.8/dns")]
    [InlineData("172.32.0.1")]
    [InlineData("http://[2606:4700::1111]/x")]
    [InlineData("www.api-xpay.com/x402/ping")]
    [InlineData("example.com.")]
    public void PublicHost_AcceptsPublicNamesAndAddresses(string input)
    {
        PublicHost.IsPublic(ServiceIdentifier.Normalize(input)).Should().BeTrue();
    }
}
