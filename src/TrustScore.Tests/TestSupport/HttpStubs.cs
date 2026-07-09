using System.Numerics;
using System.Text;

namespace TrustScore.Tests.TestSupport;

/// <summary>
/// Deterministic in-process HTTP handler: routes each request through a caller-supplied
/// responder, so outbound-HTTP components (DidWebResolver, SeedProber) can be exercised
/// end-to-end without a network. Throwing from the responder simulates a transport failure.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _respond = respond;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(_respond(request));
}

internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

/// <summary>
/// Base58btc encoder (bitcoin alphabet), the inverse of the production decoder — used to build
/// standard-conformant publicKeyMultibase values from freshly generated Ed25519 keys.
/// </summary>
internal static class Base58Encoder
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string Encode(byte[] data)
    {
        var value = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        var sb = new StringBuilder();
        while (value > 0)
        {
            sb.Insert(0, Alphabet[(int)(value % 58)]);
            value /= 58;
        }
        foreach (var b in data)
        {
            if (b != 0) break;
            sb.Insert(0, '1');
        }
        return sb.ToString();
    }
}
