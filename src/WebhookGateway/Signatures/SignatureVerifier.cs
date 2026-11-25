using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebhookGateway.Signatures;

/// <summary>
/// Checks the HMAC-SHA256 signatures that webhook senders attach. Every comparison is
/// constant-time, and an empty secret never verifies (a missing config must not accept everything).
/// </summary>
public static class SignatureVerifier
{
    /// <summary>Signed Stripe requests older or newer than this are treated as replays.</summary>
    public static readonly TimeSpan StripeTolerance = TimeSpan.FromMinutes(5);

    /// <summary>Stripe-Signature: "t=&lt;unix time&gt;,v1=&lt;hex HMAC of "t.body"&gt;", with several v1 values while a secret is rolled.</summary>
    public static bool Stripe(string? header, byte[] body, string secret, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(header) || secret.Length == 0)
        {
            return false;
        }

        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var pair = part.Trim().Split('=', 2);
            if (pair is ["t", var t] && long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                timestamp = parsed;
            }
            else if (pair is ["v1", var signature])
            {
                signatures.Add(signature);
            }
        }

        if (timestamp is null || Math.Abs(now.ToUnixTimeSeconds() - timestamp.Value) > StripeTolerance.TotalSeconds)
        {
            return false;
        }

        var expected = Hmac(secret, [.. Encoding.UTF8.GetBytes($"{timestamp}."), .. body]);
        return signatures.Any(signature => Matches(expected, signature));
    }

    /// <summary>X-Hub-Signature-256: "sha256=&lt;hex HMAC of body&gt;".</summary>
    public static bool GitHub(string? header, byte[] body, string secret) =>
        header is not null && header.StartsWith("sha256=", StringComparison.Ordinal)
        && secret.Length > 0
        && Matches(Hmac(secret, body), header["sha256=".Length..]);

    /// <summary>X-Signature-256: hex HMAC of body. For senders without their own scheme.</summary>
    public static bool Hmac(string? header, byte[] body, string secret) =>
        header is not null && secret.Length > 0 && Matches(Hmac(secret, body), header);

    private static byte[] Hmac(string secret, byte[] data) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data);

    private static bool Matches(byte[] expected, string hex)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
