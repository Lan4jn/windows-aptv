namespace Iptv.Core.Security;

/// <summary>
/// Abstraction for protecting and unprotecting serialized library state payload
/// (containing sensitive URLs, headers, tokens, and credentials).
/// </summary>
public interface ILibraryStateProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}
