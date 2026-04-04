namespace TrustScore.Core.Models;

/// <summary>
/// Normalizes service identifiers from various formats to a canonical domain form.
///
/// Accepts:
///   - URLs: https://api.example.com/v1/translate → api.example.com
///   - Domains: api.example.com → api.example.com
///   - DIDs: did:web:api.example.com → api.example.com
///
/// The canonical form is always a lowercase domain without protocol, path, or port.
/// </summary>
public static class ServiceIdentifier
{
    private const int MaxLength = 500;

    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Service identifier cannot be empty", nameof(input));

        if (input.Length > MaxLength)
            throw new ArgumentException($"Service identifier too long (max {MaxLength} characters)", nameof(input));

        var trimmed = input.Trim();

        // did:web:api.example.com → api.example.com
        if (trimmed.StartsWith("did:web:", StringComparison.OrdinalIgnoreCase))
            return trimmed["did:web:".Length..].ToLowerInvariant();

        // https://api.example.com/v1/foo → api.example.com
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            && (uri.Scheme == "http" || uri.Scheme == "https"))
            return uri.Host.ToLowerInvariant();

        // api.example.com:8080/path → api.example.com
        var domain = trimmed.Split('/', 2)[0]  // Remove path
                            .Split(':', 2)[0]; // Remove port

        return domain.ToLowerInvariant();
    }

    /// <summary>
    /// Convert a canonical domain back to did:web format (for receipt verification).
    /// </summary>
    public static string ToDid(string canonical)
        => $"did:web:{canonical}";
}
