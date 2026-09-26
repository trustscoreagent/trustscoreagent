using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace TrustScore.Api.Logging;

/// <summary>
/// One JSON object per line, shaped for Cloud Logging.
///
/// The stock JSON console formatter writes <c>LogLevel</c> but not <c>severity</c>, and Cloud
/// Logging only reads the latter. Every application line therefore landed with no severity, so
/// <c>severity&gt;=ERROR</c> filters, Error Reporting and log-based alerts never saw an application
/// error, including the Merkle integrity and consistency errors the anchoring job raises.
///
/// This writes <c>severity</c> and <c>message</c> (the two fields Cloud Logging promotes), and keeps
/// the fields the stock formatter wrote (<c>Message</c>, <c>LogLevel</c>, <c>Category</c>,
/// <c>EventId</c>, <c>State</c>) so existing queries on <c>jsonPayload.Message</c> keep working.
/// Values go through <see cref="Utf8JsonWriter"/>, so control characters are escaped exactly as
/// before (no log injection).
/// </summary>
public sealed class CloudLoggingJsonFormatter : ConsoleFormatter
{
    public const string FormatterName = "cloud-logging-json";

    public CloudLoggingJsonFormatter() : base(FormatterName) { }

    public static string Severity(LogLevel level) => level switch
    {
        LogLevel.Trace or LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARNING",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "DEFAULT",
    };

    public override void Write<TState>(
        in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
            return;

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("severity", Severity(logEntry.LogLevel));

            // Cloud Logging shows `message` as the entry summary; for errors it must carry the
            // stack trace for Error Reporting to group them.
            var summary = logEntry.Exception is null ? message : $"{message}\n{logEntry.Exception}";
            json.WriteString("message", summary);

            json.WriteString("Message", message);
            json.WriteString("LogLevel", logEntry.LogLevel.ToString());
            json.WriteString("Category", logEntry.Category);
            json.WriteNumber("EventId", logEntry.EventId.Id);
            if (logEntry.Exception is not null)
                json.WriteString("Exception", logEntry.Exception.ToString());

            if (logEntry.State is IReadOnlyCollection<KeyValuePair<string, object?>> state)
            {
                json.WriteStartObject("State");
                foreach (var (key, value) in state)
                    WriteValue(json, key, value);
                json.WriteEndObject();
            }

            json.WriteEndObject();
        }

        textWriter.Write(Encoding.UTF8.GetString(buffer.ToArray()));
        textWriter.Write(Environment.NewLine);
    }

    private static void WriteValue(Utf8JsonWriter json, string key, object? value)
    {
        switch (value)
        {
            case null: json.WriteNull(key); break;
            case bool b: json.WriteBoolean(key, b); break;
            case int i: json.WriteNumber(key, i); break;
            case long l: json.WriteNumber(key, l); break;
            case double d when double.IsFinite(d): json.WriteNumber(key, d); break;
            default: json.WriteString(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
        }
    }
}
