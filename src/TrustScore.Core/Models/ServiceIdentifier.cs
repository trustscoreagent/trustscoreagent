namespace TrustScore.Core.Models;

/// <summary>
/// Normalizes service identifiers with two levels of granularity:
///
/// - Endpoint level (with path): api.example.com/v1/translate
///   → Used for ratings and per-endpoint scores
///
/// - Provider level (domain only): api.example.com
///   → Used for aggregated provider-wide scores
///
/// Accepts any format: URL, domain, domain+path, DID.
/// Query parameters and fragments are always stripped.
/// </summary>
public static class ServiceIdentifier
{
    private const int MaxLength = 500;

    /// <summary>
    /// Normalize to the most specific level: includes path if present.
    /// https://api.example.com/v1/translate?key=123 → api.example.com/v1/translate
    /// api.example.com → api.example.com (no path = provider level)
    /// </summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Service identifier cannot be empty", nameof(input));

        if (input.Length > MaxLength)
            throw new ArgumentException($"Service identifier too long (max {MaxLength} characters)", nameof(input));

        var trimmed = input.Trim();

        // did:web:api.example.com → api.example.com (no path in DID format)
        if (trimmed.StartsWith("did:web:", StringComparison.OrdinalIgnoreCase))
            return trimmed["did:web:".Length..].ToLowerInvariant();

        // Full URL: https://api.example.com/v1/translate?key=123
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == "http" || uri.Scheme == "https"))
        {
            var host = uri.Host.ToLowerInvariant();
            // Lowercase the path too, matching the raw-input branch below. Otherwise the same
            // endpoint given as a URL vs. raw string normalizes to two different identifiers and
            // its reputation is split across two rows.
            var path = uri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
            return path is "" or "/" ? host : $"{host}{path}";
        }

        // Raw: api.example.com:8080/v1/translate
        var withoutPort = trimmed;
        var firstSlash = trimmed.IndexOf('/');
        if (firstSlash > 0)
        {
            var hostPart = trimmed[..firstSlash];
            var pathPart = trimmed[firstSlash..].TrimEnd('/');
            // Strip port from host
            var colonIdx = hostPart.IndexOf(':');
            if (colonIdx > 0) hostPart = hostPart[..colonIdx];
            return (pathPart is "" or "/" ? hostPart : $"{hostPart}{pathPart}").ToLowerInvariant();
        }

        // Domain only: api.example.com or api.example.com:8080
        var domain = trimmed.Split(':')[0];
        return domain.ToLowerInvariant();
    }

    /// <summary>
    /// Extract the provider (domain only) from any identifier.
    /// api.example.com/v1/translate → api.example.com
    /// api.example.com → api.example.com
    /// </summary>
    public static string ExtractProvider(string normalizedId)
    {
        var slashIdx = normalizedId.IndexOf('/');
        return slashIdx > 0 ? normalizedId[..slashIdx] : normalizedId;
    }

    /// <summary>
    /// Check if an identifier is provider-level (no path) or endpoint-level (has path).
    /// </summary>
    public static bool IsProviderLevel(string normalizedId)
        => !normalizedId.Contains('/');

    /// <summary>
    /// Convert a canonical form back to did:web format (for receipt verification).
    /// Only uses the domain part.
    /// </summary>
    public static string ToDid(string canonical)
        => $"did:web:{ExtractProvider(canonical)}";
}
