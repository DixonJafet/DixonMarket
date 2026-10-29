using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace MarketDixon.Api;

internal sealed class PasswordHasher
{
    private const int Iterations = 210_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const string Prefix = "pbkdf2-sha256";

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Prefix}${Iterations}${WebEncoders.Base64UrlEncode(salt)}${WebEncoders.Base64UrlEncode(hash)}";
    }

    public bool Verify(string password, string encodedHash)
    {
        var parts = encodedHash.Split('$');
        if (parts.Length != 4 ||
            parts[0] != Prefix ||
            !int.TryParse(parts[1], out var iterations) ||
            iterations is < 100_000 or > 1_000_000)
        {
            return false;
        }

        try
        {
            var salt = WebEncoders.Base64UrlDecode(parts[2]);
            var expected = WebEncoders.Base64UrlDecode(parts[3]);
            if (salt.Length != SaltSize || expected.Length != HashSize)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, HashSize);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed class TokenService(byte[] signingKey)
{
    private const string Issuer = "MarketDixon.Api";
    private const string Audience = "MarketDixon.Client";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static byte[] GetSigningKey(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Jwt:SigningKey"]
            ?? Environment.GetEnvironmentVariable("MARKETDIXON_JWT_SIGNING_KEY");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var key = Encoding.UTF8.GetBytes(configured);
            if (key.Length < 32)
            {
                throw new InvalidOperationException("MARKETDIXON_JWT_SIGNING_KEY must contain at least 32 UTF-8 bytes.");
            }

            return key;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException("Configure MARKETDIXON_JWT_SIGNING_KEY before running outside Development.");
        }

        var developmentKey = RandomNumberGenerator.GetBytes(32);
        Console.Error.WriteLine("WARNING: using an ephemeral development signing key; development tokens expire when the API restarts.");
        return developmentKey;
    }

    public string Create(Guid userId, string userName)
    {
        var now = DateTimeOffset.UtcNow;
        var header = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }, JsonOptions));
        var payload = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new TokenPayload(
            Issuer,
            Audience,
            userId.ToString(),
            userName,
            now.ToUnixTimeSeconds(),
            now.AddHours(2).ToUnixTimeSeconds()), JsonOptions));
        var unsignedToken = $"{header}.{payload}";
        var signature = HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes(unsignedToken));
        return $"{unsignedToken}.{WebEncoders.Base64UrlEncode(signature)}";
    }

    public IEnumerable<Claim>? Validate(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var header = JsonSerializer.Deserialize<TokenHeader>(WebEncoders.Base64UrlDecode(parts[0]), JsonOptions);
            if (header is not { Algorithm: "HS256", Type: "JWT" })
            {
                return null;
            }

            var expected = HMACSHA256.HashData(signingKey, Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"));
            var actual = WebEncoders.Base64UrlDecode(parts[2]);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<TokenPayload>(WebEncoders.Base64UrlDecode(parts[1]), JsonOptions);
            if (payload is null ||
                payload.Issuer != Issuer ||
                payload.Audience != Audience ||
                payload.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
                !Guid.TryParse(payload.Subject, out var userId) ||
                string.IsNullOrWhiteSpace(payload.UserName))
            {
                return null;
            }

            return
            [
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Name, payload.UserName),
                new Claim("token_expires_at", payload.ExpiresAt.ToString(System.Globalization.CultureInfo.InvariantCulture))
            ];
        }

        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed record TokenHeader(
        [property: JsonPropertyName("alg")] string Algorithm,
        [property: JsonPropertyName("typ")] string Type);

    private sealed record TokenPayload(
        [property: JsonPropertyName("iss")]
        string Issuer,
        [property: JsonPropertyName("aud")]
        string Audience,
        [property: JsonPropertyName("sub")]
        string Subject,
        [property: JsonPropertyName("userName")]
        string UserName,
        [property: JsonPropertyName("iat")]
        long IssuedAt,
        [property: JsonPropertyName("exp")]
        long ExpiresAt);
}
