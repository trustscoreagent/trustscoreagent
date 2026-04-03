namespace TrustScore.Core.Interfaces;

public interface IDidResolver
{
    /// <summary>
    /// Resolves a did:web DID to its public key bytes (Ed25519).
    /// Returns null if resolution fails.
    /// </summary>
    Task<byte[]?> ResolvePublicKeyAsync(string did);
}
