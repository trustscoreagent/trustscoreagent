using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using TrustScore.Core.Interfaces;

namespace TrustScore.Api.Receipts;

public sealed class DidWebResolver : IDidResolver
{
    public const string HttpClientName = "did-web";

    // Cap on the did.json body we will read. A DID document is a few hundred bytes;
    // anything larger is either malicious or misconfigured.
    private const int MaxResponseBytes = 64 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ICacheService _cache;
    private readonly ILogger<DidWebResolver> _logger;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public DidWebResolver(IHttpClientFactory httpClientFactory, ICacheService cache, ILogger<DidWebResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
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

            // Reject obviously private/reserved hosts early (literal IPs, localhost) for a clean
            // log; the real SSRF protection is the ConnectCallback on the named HttpClient, which
            // validates every DNS-resolved IP and is not bypassable via DNS rebinding.
            if (IsPrivateOrReservedHost(domain))
            {
                _logger.LogWarning("DID resolution blocked for private host: {Domain}", domain);
                return null;
            }

            if (!Uri.TryCreate($"https://{domain}/.well-known/did.json", UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps)
            {
                _logger.LogWarning("DID resolution blocked, invalid did:web host: {Domain}", domain);
                return null;
            }

            var httpClient = _httpClientFactory.CreateClient(HttpClientName);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Redirects are disabled on the handler, so a 3xx is treated as a non-success response
            // rather than followed to an unvalidated target.
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("DID resolution failed for {Did}: HTTP {StatusCode}", did, response.StatusCode);
                return null;
            }

            if (response.Content.Headers.ContentLength is long declared && declared > MaxResponseBytes)
            {
                _logger.LogWarning("DID document too large for {Did}: {Bytes} bytes", did, declared);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
            var json = await ReadBoundedAsync(stream, MaxResponseBytes, cts.Token);
            if (json is null)
            {
                _logger.LogWarning("DID document exceeded {Max} bytes for {Did}", MaxResponseBytes, did);
                return null;
            }

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

                var keyBytes = ExtractEd25519Key(method);
                if (keyBytes is not null)
                {
                    await _cache.SetAsync(cacheKey, Convert.ToBase64String(keyBytes), CacheTtl);
                    return keyBytes;
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
    /// Extracts a raw 32-byte Ed25519 public key from a verification method, supporting the
    /// standard encodings: publicKeyMultibase (2020, base58btc of the 0xED01 multicodec prefix +
    /// key), publicKeyBase58 (2018), publicKeyJwk (JsonWebKey2020, OKP/Ed25519, x = base64url), and
    /// the non-standard publicKeyBase64 kept for backwards compatibility. Returns null if no field
    /// yields a valid 32-byte key.
    /// </summary>
    internal static byte[]? ExtractEd25519Key(JsonElement method)
    {
        if (method.TryGetProperty("publicKeyMultibase", out var multibase))
        {
            var keyStr = multibase.GetString();
            if (keyStr is not null && keyStr.StartsWith('z'))
            {
                try { return NormalizeEd25519(Base58.Decode(keyStr[1..])); }
                catch (FormatException) { /* fall through to other fields */ }
            }
        }

        if (method.TryGetProperty("publicKeyBase58", out var base58))
        {
            var keyStr = base58.GetString();
            if (keyStr is not null)
            {
                try { return NormalizeEd25519(Base58.Decode(keyStr)); }
                catch (FormatException) { }
            }
        }

        if (method.TryGetProperty("publicKeyJwk", out var jwk))
        {
            if (jwk.TryGetProperty("kty", out var kty) && kty.GetString() == "OKP"
                && jwk.TryGetProperty("crv", out var crv) && crv.GetString() == "Ed25519"
                && jwk.TryGetProperty("x", out var x) && x.GetString() is { } xStr)
            {
                try { return NormalizeEd25519(Base64UrlDecode(xStr)); }
                catch (FormatException) { }
            }
        }

        if (method.TryGetProperty("publicKeyBase64", out var base64Key))
        {
            var keyStr = base64Key.GetString();
            if (keyStr is not null)
            {
                try { return NormalizeEd25519(Convert.FromBase64String(keyStr)); }
                catch (FormatException) { }
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the raw 32-byte Ed25519 key, stripping the 0xED 0x01 multicodec prefix if present
    /// (as in Ed25519VerificationKey2020's multibase encoding). Returns null on any other length.
    /// </summary>
    private static byte[]? NormalizeEd25519(byte[] keyBytes)
    {
        if (keyBytes.Length == 34 && keyBytes[0] == 0xED && keyBytes[1] == 0x01)
            return keyBytes[2..];
        if (keyBytes.Length == 32)
            return keyBytes;
        return null;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> from the stream. Returns null if the stream
    /// produces more than the limit (so an oversized body cannot exhaust memory).
    /// </summary>
    private static async Task<string?> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Cheap pre-check for literal private IPs / localhost in the host. Defense-in-depth only;
    /// hosts given as domain names are validated at connect time by <see cref="SsrfGuard"/>.
    /// </summary>
    private static bool IsPrivateOrReservedHost(string domain)
    {
        var host = domain.Split('/')[0].Split(':')[0].ToLowerInvariant(); // strip path + port

        if (host is "localhost" or "")
            return true;

        if (IPAddress.TryParse(host, out var ip))
            return SsrfGuard.IsBlocked(ip);

        return false;
    }
}

/// <summary>
/// SSRF guard for outbound did:web resolution. Validates the actual IP addresses a host
/// resolves to and only connects to public ones, closing DNS-rebinding/TOCTOU gaps that a
/// host-string check cannot.
/// </summary>
internal static class SsrfGuard
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = await Dns.GetHostAddressesAsync(endpoint.Host, ct);
        var allowed = addresses.Where(a => !IsBlocked(a)).ToArray();
        if (allowed.Length == 0)
            throw new IOException($"SSRF guard: host '{endpoint.Host}' resolves only to blocked addresses");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            // Connect only to the vetted IPs — never re-resolve, so the address checked is the
            // address connected to.
            await socket.ConnectAsync(allowed, endpoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static bool IsBlocked(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return true;                                   // 0.0.0.0/8
            if (b[0] == 10) return true;                                  // 10.0.0.0/8
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;    // 100.64.0.0/10 CGNAT
            if (b[0] == 127) return true;                                 // loopback
            if (b[0] == 169 && b[1] == 254) return true;                 // link-local + cloud metadata
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;     // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;                 // 192.168.0.0/16
            if (b[0] >= 224) return true;                                 // multicast + reserved
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6Loopback)) return true;
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) return true;                       // fc00::/7 unique local
            // 64:ff9b::/96 NAT64: block so 64:ff9b::a9fe:a9fe (→169.254.169.254) can't reach metadata.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b &&
                b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0 &&
                b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0) return true;
            return false;
        }

        return true; // unknown address family → block
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
