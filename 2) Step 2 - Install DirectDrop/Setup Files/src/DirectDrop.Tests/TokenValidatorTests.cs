using DirectDrop.Core;
using Xunit;

namespace DirectDrop.Tests;

public class TokenValidatorTests
{
    [Fact]
    public void GenerateToken_produces_a_non_trivial_random_value()
    {
        string token = TokenValidator.GenerateToken();
        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.True(token.Length >= 32, "Token should carry meaningful entropy (32 raw bytes, base64url-encoded).");
    }

    [Fact]
    public void GenerateToken_never_repeats_across_many_calls()
    {
        var tokens = new HashSet<string>();
        for (int i = 0; i < 1000; i++)
            Assert.True(tokens.Add(TokenValidator.GenerateToken()), "Generated a duplicate token - RNG or entropy problem.");
    }

    [Fact]
    public void GenerateToken_is_URL_safe()
    {
        string token = TokenValidator.GenerateToken();
        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
    }

    [Fact]
    public void IsValid_accepts_the_exact_matching_token()
    {
        string token = TokenValidator.GenerateToken();
        Assert.True(TokenValidator.IsValid(token, token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-token")]
    public void IsValid_rejects_missing_or_wrong_tokens(string? provided)
    {
        string expected = TokenValidator.GenerateToken();
        Assert.False(TokenValidator.IsValid(provided, expected));
    }

    [Fact]
    public void IsValid_rejects_a_token_that_differs_only_in_the_last_character()
    {
        string expected = TokenValidator.GenerateToken();
        string almostRight = expected[..^1] + (expected[^1] == 'A' ? 'B' : 'A');
        Assert.False(TokenValidator.IsValid(almostRight, expected));
    }

    [Fact]
    public void IsValid_rejects_a_different_length_token_without_throwing()
    {
        string expected = TokenValidator.GenerateToken();
        Assert.False(TokenValidator.IsValid(expected + "extra", expected));
        Assert.False(TokenValidator.IsValid(expected[..10], expected));
    }
}
