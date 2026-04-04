using System.Text.Json;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Receipts;

public sealed class DidWebResolver : IDidResolver
{
    private readonly HttpClient _httpClient;
    private readonly ICacheService _cache;
    private readonly ILogger<DidWebResolver> _logger;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public DidWebResolver(HttpClient httpClient, ICacheService cache, ILogger<DidWebResolver> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<byte[]?> ResolvePublicKeyAsync(string did)
    {
        if (!did.StartsWith("did:web:"))
        {
            _logger.LogWarning("Unsupported DID method: {Did}", did);
            return null;
        }

        // Check cache first
        var cacheKey = $"did-key:{did}";
        var cached = await _cache.GetAsync(cacheKey);
        if (cached is not null)
            return Convert.FromBase64String(cached);

        try
        {
            // did:web:api.example.com → https://api.example.com/.well-known/did.json
            var domain = did["did:web:".Length..];

            // SSRF protection: reject private/internal domains
            if (IsPrivateOrReservedDomain(domain))
            {
                _logger.LogWarning("DID resolution blocked for private domain: {Domain}", domain);
                return null;
            }

            var url = $"https://{domain}/.well-known/did.json";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var response = await _httpClient.GetAsync(url, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("DID resolution failed for {Did}: HTTP {StatusCode}", did, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonSerializer.Deserialize<JsonElement>(json);

            // Extract the first Ed25519 verification method
            if (!doc.TryGetProperty("verificationMethod", out var methods))
                return null;

            foreach (var method in methods.EnumerateArray())
            {
                if (!method.TryGetProperty("type", out var type))
                    continue;

                var typeStr = type.GetString();
                if (typeStr is not ("Ed25519VerificationKey2020" or "Ed25519VerificationKey2018" or "JsonWebKey2020"))
                    continue;

                // Try publicKeyMultibase (preferred)
                if (method.TryGetProperty("publicKeyMultibase", out var multibase))
                {
                    var keyStr = multibase.GetString();
                    if (keyStr is not null && keyStr.StartsWith("z"))
                    {
                        // Multibase z prefix = base58btc encoded
                        var keyBytes = Base58.Decode(keyStr[1..]);

                        // Cache the key
                        await _cache.SetAsync(cacheKey, Convert.ToBase64String(keyBytes), CacheTtl);
                        return keyBytes;
                    }
                }

                // Try publicKeyBase64
                if (method.TryGetProperty("publicKeyBase64", out var base64Key))
                {
                    var keyStr = base64Key.GetString();
                    if (keyStr is not null)
                    {
                        var keyBytes = Convert.FromBase64String(keyStr);
                        await _cache.SetAsync(cacheKey, Convert.ToBase64String(keyBytes), CacheTtl);
                        return keyBytes;
                    }
                }
            }

            _logger.LogWarning("No Ed25519 key found in DID Document for {Did}", did);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DID resolution error for {Did}", did);
            return null;
        }
    }

    /// <summary>
    /// Block SSRF: reject localhost, private IPs, and reserved domains.
    /// </summary>
    private static bool IsPrivateOrReservedDomain(string domain)
    {
        var host = domain.Split(':')[0].ToLowerInvariant(); // Strip port

        if (host is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0")
            return true;

        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            var bytes = ip.GetAddressBytes();
            // 10.x.x.x
            if (bytes[0] == 10) return true;
            // 172.16.x.x - 172.31.x.x
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.x.x
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // 169.254.x.x (link-local, AWS metadata)
            if (bytes[0] == 169 && bytes[1] == 254) return true;
        }

        return false;
    }
}

/// <summary>
/// Minimal Base58 decoder for multibase-encoded public keys.
/// </summary>
internal static class Base58
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static byte[] Decode(string input)
    {
        if (input.Length > 256)
            throw new FormatException("Base58 input too long (max 256 characters)");

        var bi = System.Numerics.BigInteger.Zero;
        foreach (var c in input)
        {
            var index = Alphabet.IndexOf(c);
            if (index < 0) throw new FormatException($"Invalid Base58 character: {c}");
            bi = bi * 58 + index;
        }

        var bytes = bi.ToByteArray(isUnsigned: true, isBigEndian: true);

        // Count leading '1's (which represent leading zero bytes)
        var leadingZeros = input.TakeWhile(c => c == '1').Count();
        if (leadingZeros > 0)
        {
            var result = new byte[leadingZeros + bytes.Length];
            bytes.CopyTo(result, leadingZeros);
            return result;
        }

        return bytes;
    }
}
