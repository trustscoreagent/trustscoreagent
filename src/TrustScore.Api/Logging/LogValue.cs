namespace TrustScore.Api.Logging;

/// <summary>
/// Makes a caller-supplied value (an agent DID, a service id, a receipt nonce) safe to log.
///
/// The production JSON formatter already escapes control characters, but the plain console
/// formatter used in Development writes values verbatim, so a DID containing a newline could forge
/// extra log lines there. Cleaning at the call site protects every formatter, and keeps an
/// oversized header from filling the logs.
/// </summary>
public static class LogValue
{
    private const int MaxLength = 200;

    public static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        var cleaned = value.Replace("\r", " ").Replace("\n", " ");
        return cleaned.Length <= MaxLength ? cleaned : cleaned[..MaxLength] + "…";
    }
}
