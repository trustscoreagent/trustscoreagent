using FluentAssertions;
using TrustScore.Api.Jobs;
using Xunit;

namespace TrustScore.Tests.Unit;

public class SeedProberTests
{
    // --- empty / missing body ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyBody_IsNeverValid(string? body)
    {
        SeedProber.ValidateBody(body!, "current").Should().BeFalse();
        SeedProber.ValidateBody(body!, null).Should().BeFalse();
    }

    // --- no ExpectField: non-empty body counts, but an empty JSON array does not ---

    [Fact]
    public void NoExpectField_PlainText_IsValid()
        => SeedProber.ValidateBody("just keep shipping", null).Should().BeTrue();

    [Fact]
    public void NoExpectField_JsonObject_IsValid()
        => SeedProber.ValidateBody("{\"a\":1}", "").Should().BeTrue();

    [Fact]
    public void NoExpectField_NonEmptyArray_IsValid()
        => SeedProber.ValidateBody("[{\"date\":\"2026-01-01\"}]", null).Should().BeTrue();

    [Fact]
    public void NoExpectField_EmptyArray_IsNotValid()
        => SeedProber.ValidateBody("[]", null).Should().BeFalse();

    // --- ExpectField present: top-level property ---

    [Fact]
    public void ExpectField_PresentTopLevel_IsValid()
        => SeedProber.ValidateBody("{\"current\":{\"temperature_2m\":7.1},\"other\":1}", "current").Should().BeTrue();

    [Fact]
    public void ExpectField_MissingTopLevel_IsNotValid()
        => SeedProber.ValidateBody("{\"hourly\":{}}", "current").Should().BeFalse();

    // --- ExpectField on an array root descends into the first element ---

    [Fact]
    public void ExpectField_OnArrayRoot_ChecksFirstElement()
        => SeedProber.ValidateBody("[{\"name\":{\"common\":\"France\"}}]", "name").Should().BeTrue();

    [Fact]
    public void ExpectField_OnEmptyArrayRoot_IsNotValid()
        => SeedProber.ValidateBody("[]", "name").Should().BeFalse();

    // --- nested dot-path ---

    [Fact]
    public void ExpectField_NestedPath_Present_IsValid()
        => SeedProber.ValidateBody("{\"rates\":{\"USD\":1.08}}", "rates.USD").Should().BeTrue();

    [Fact]
    public void ExpectField_NestedPath_Missing_IsNotValid()
        => SeedProber.ValidateBody("{\"rates\":{\"GBP\":0.85}}", "rates.USD").Should().BeFalse();

    // --- malformed / type mismatches ---

    [Fact]
    public void ExpectField_OnMalformedJson_IsNotValid()
        => SeedProber.ValidateBody("not json at all", "current").Should().BeFalse();

    [Fact]
    public void ExpectField_DescendingIntoNonObject_IsNotValid()
        => SeedProber.ValidateBody("{\"rates\":5}", "rates.USD").Should().BeFalse();
}
