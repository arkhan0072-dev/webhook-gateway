using System.Security.Cryptography;
using System.Text;
using WebhookGateway.Signatures;

namespace WebhookGateway.Tests;

public class SignatureTests
{
    private const string Secret = "whsec_test";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"id":"evt_1"}""");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    internal static string Hex(string secret, byte[] data) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), data));

    private static string StripeHeader(long timestamp, string secret = Secret) =>
        $"t={timestamp},v1={Hex(secret, [.. Encoding.UTF8.GetBytes($"{timestamp}."), .. Body])}";

    [Fact]
    public void Stripe_accepts_a_valid_signature() =>
        Assert.True(SignatureVerifier.Stripe(StripeHeader(Now.ToUnixTimeSeconds()), Body, Secret, Now));

    [Fact]
    public void Stripe_accepts_any_matching_v1_while_a_secret_is_rolled() =>
        Assert.True(SignatureVerifier.Stripe(
            StripeHeader(Now.ToUnixTimeSeconds(), "whsec_old") + "," + StripeHeader(Now.ToUnixTimeSeconds()).Split(',')[1],
            Body, Secret, Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("v1=abc")]
    [InlineData("t=soon,v1=abc")]
    public void Stripe_rejects_malformed_headers(string? header) =>
        Assert.False(SignatureVerifier.Stripe(header, Body, Secret, Now));

    [Fact]
    public void Stripe_rejects_a_wrong_secret() =>
        Assert.False(SignatureVerifier.Stripe(StripeHeader(Now.ToUnixTimeSeconds(), "whsec_wrong"), Body, Secret, Now));

    [Fact]
    public void Stripe_rejects_a_tampered_body() =>
        Assert.False(SignatureVerifier.Stripe(StripeHeader(Now.ToUnixTimeSeconds()), Encoding.UTF8.GetBytes("""{"id":"evt_2"}"""), Secret, Now));

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    public void Stripe_rejects_timestamps_outside_five_minutes(int offset) =>
        Assert.False(SignatureVerifier.Stripe(StripeHeader(Now.ToUnixTimeSeconds() + offset), Body, Secret, Now));

    [Fact]
    public void Stripe_accepts_the_edge_of_the_window() =>
        Assert.True(SignatureVerifier.Stripe(StripeHeader(Now.ToUnixTimeSeconds() - 300), Body, Secret, Now));

    [Fact]
    public void GitHub_accepts_a_valid_signature() =>
        Assert.True(SignatureVerifier.GitHub("sha256=" + Hex(Secret, Body), Body, Secret));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha1=abc")]
    [InlineData("sha256=")]
    [InlineData("sha256=0000")]
    public void GitHub_rejects_bad_signatures(string? header) =>
        Assert.False(SignatureVerifier.GitHub(header, Body, Secret));

    [Fact]
    public void Hmac_accepts_a_valid_signature() =>
        Assert.True(SignatureVerifier.Hmac(Hex(Secret, Body), Body, Secret));

    [Fact]
    public void Hmac_accepts_upper_case_hex() =>
        Assert.True(SignatureVerifier.Hmac(Hex(Secret, Body).ToUpperInvariant(), Body, Secret));

    [Theory]
    [InlineData(null)]
    [InlineData("not-hex")]
    [InlineData("00")]
    public void Hmac_rejects_bad_signatures(string? header) =>
        Assert.False(SignatureVerifier.Hmac(header, Body, Secret));

    [Fact]
    public void An_empty_secret_never_verifies()
    {
        Assert.False(SignatureVerifier.GitHub("sha256=" + Hex("", Body), Body, ""));
        Assert.False(SignatureVerifier.Hmac(Hex("", Body), Body, ""));
        Assert.False(SignatureVerifier.Stripe(StripeHeader(Now.ToUnixTimeSeconds(), ""), Body, "", Now));
    }
}
