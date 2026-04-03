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
            var url = $"https://{domain}/.well-known/did.json";

            var response = await _httpClient.GetAsync(url);
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
}

/// <summary>
/// Minimal Base58 decoder for multibase-encoded public keys.
/// </summary>
internal static class Base58
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static byte[] Decode(string input)
    {
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
