namespace WebhookGateway;

/// <summary>The "Gateway" section of configuration.</summary>
public class GatewayOptions
{
    /// <summary>Sent as X-Api-Key to the admin endpoints.</summary>
    public string AdminApiKey { get; set; } = "";

    public bool RunDeliveryWorker { get; set; } = true;

    /// <summary>After this many failed attempts a delivery is dead-lettered.</summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>Wait after the first failure; doubled after each further failure.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Where webhooks come from, keyed by the name used in the URL: /webhooks/{name}.</summary>
    public Dictionary<string, SourceOptions> Sources { get; set; } = [];

    /// <summary>Services that receive the forwarded webhooks, keyed by name.</summary>
    public Dictionary<string, DestinationOptions> Destinations { get; set; } = [];
}

public enum SignatureScheme
{
    Stripe,
    GitHub,
    Hmac,
}

public class SourceOptions
{
    public SignatureScheme Scheme { get; set; }

    public string Secret { get; set; } = "";

    /// <summary>Names of the destinations that get every event from this source.</summary>
    public List<string> Destinations { get; set; } = [];
}

public class DestinationOptions
{
    public string Url { get; set; } = "";

    /// <summary>Used to sign forwarded requests, so the destination can tell they came from the gateway.</summary>
    public string Secret { get; set; } = "";
}
