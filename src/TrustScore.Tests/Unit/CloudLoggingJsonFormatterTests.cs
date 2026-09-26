using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TrustScore.Api.Logging;
using Xunit;

namespace TrustScore.Tests.Unit;

public class CloudLoggingJsonFormatterTests
{
    private static JsonElement Format(LogLevel level, string text, Exception? ex = null,
        params (string Key, object? Value)[] state)
    {
        var values = state.Select(s => new KeyValuePair<string, object?>(s.Key, s.Value)).ToList();
        var entry = new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(
            level, "TrustScore.Api.Jobs.HourlyJob", new EventId(7), values, ex, (_, _) => text);
        using var writer = new StringWriter();
        new CloudLoggingJsonFormatter().Write(entry, null, writer);
        var line = writer.ToString();
        line.TrimEnd().Should().NotContain("\n", "one entry per line");
        return JsonDocument.Parse(line).RootElement;
    }

    [Theory]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Information, "INFO")]
    [InlineData(LogLevel.Warning, "WARNING")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Critical, "CRITICAL")]
    public void WritesTheSeverityCloudLoggingReads(LogLevel level, string severity)
        => Format(level, "x").GetProperty("severity").GetString().Should().Be(severity);

    [Fact]
    public void KeepsTheFieldsExistingQueriesUse()
    {
        var json = Format(LogLevel.Error, "Merkle consistency: broken", null, ("Previous", 12), ("Root", "ab"));

        json.GetProperty("message").GetString().Should().Be("Merkle consistency: broken");
        json.GetProperty("Message").GetString().Should().Be("Merkle consistency: broken");
        json.GetProperty("LogLevel").GetString().Should().Be("Error");
        json.GetProperty("Category").GetString().Should().Be("TrustScore.Api.Jobs.HourlyJob");
        json.GetProperty("State").GetProperty("Previous").GetInt32().Should().Be(12);
        json.GetProperty("State").GetProperty("Root").GetString().Should().Be("ab");
    }

    [Fact]
    public void AttachesTheStackTraceForErrorReporting()
    {
        Exception ex;
        try { throw new InvalidOperationException("boom"); } catch (Exception e) { ex = e; }

        var json = Format(LogLevel.Error, "step failed", ex);

        json.GetProperty("message").GetString().Should().Contain("step failed").And.Contain("InvalidOperationException: boom");
        json.GetProperty("Exception").GetString().Should().Contain("boom");
    }

    [Fact]
    public void EscapesNewlinesInUserControlledValues()
    {
        // A forged line must stay inside the JSON string, not become a second log entry.
        var json = Format(LogLevel.Warning, "did:web:evil\n{\"severity\":\"INFO\"}");
        json.GetProperty("severity").GetString().Should().Be("WARNING");
    }
}

public class DbConnectionFactoryPoolTests
{
    [Fact]
    public void CapsThePool_WhenTheConnectionStringDoesNotSetOne()
        => new Npgsql.NpgsqlConnectionStringBuilder(
                TrustScore.Api.Data.DbConnectionFactory.WithPoolCap("Host=db;Database=x;Username=u;Password=p", 3))
            .MaxPoolSize.Should().Be(3);

    [Fact]
    public void AnExplicitPoolSize_Wins()
        => new Npgsql.NpgsqlConnectionStringBuilder(
                TrustScore.Api.Data.DbConnectionFactory.WithPoolCap("Host=db;Maximum Pool Size=7", 3))
            .MaxPoolSize.Should().Be(7);
}
