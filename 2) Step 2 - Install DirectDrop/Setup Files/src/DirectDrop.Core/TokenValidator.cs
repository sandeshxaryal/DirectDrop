using System.Security.Cryptography;
using System.Text;

namespace DirectDrop.Core;

public static class TokenValidator
{
    /// <summary>
    /// Generates a new cryptographically random, URL-safe session token.
    /// 32 bytes (256 bits) of entropy, base64url-encoded with no padding.
    /// </summary>
    public static string GenerateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>
    /// Compares a token supplied by a client against the session's expected
    /// token in constant time, so a timing attack cannot be used to guess it
    /// one character at a time. Returns false (never throws) for null/empty
    /// or mismatched-length input.
    /// </summary>
    public static bool IsValid(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
            return false;

        byte[] providedBytes = Encoding.UTF8.GetBytes(provided);
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expected);

        // FixedTimeEquals requires equal-length spans. Comparing lengths first
        // does leak length via timing, but the token length is fixed and public
        // (it's always GenerateToken()'s output length), so that leak is harmless.
        if (providedBytes.Length != expectedBytes.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
