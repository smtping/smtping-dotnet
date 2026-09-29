using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smtping;

public sealed class SmtpingOptions
{
    /// <summary>Defaults to the SMTPING_API_KEY environment variable.</summary>
    public string? ApiKey { get; set; }
    public string? BaseUrl { get; set; }
    /// <summary>Per-request timeout. Default 60 seconds.</summary>
    public TimeSpan? Timeout { get; set; }
    /// <summary>Retries on network errors, 429 and 5xx. Default 3.</summary>
    public int? MaxRetries { get; set; }
    public string? UserAgent { get; set; }
    /// <summary>Bring your own HttpClient (for IHttpClientFactory). The SDK will not dispose it.</summary>
    public HttpClient? HttpClient { get; set; }
}

public sealed class WaitOptions
{
    /// <summary>Maximum wait. Default 30 minutes.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>First poll interval, grows to 30 seconds. Default 5 seconds.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);
    public Action<BulkJob>? OnProgress { get; set; }
}

/// <summary>Simple routing for a verification result.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Band
{
    /// <summary>valid, alias: send.</summary>
    Safe,
    /// <summary>invalid, spamtrap, disposable, blacklisted, complainer, spambot, inbox_full: remove.</summary>
    Avoid,
    /// <summary>catch_all, unknown, role and others: your call.</summary>
    Judgement,
}

/// <summary>Threat lists available to <see cref="SmtpingClient.CheckAsync"/>.</summary>
public static class CheckType
{
    public const string Spamtrap = "spamtrap";
    public const string Disposable = "disposable";
    public const string Spambot = "spambot";
    public const string Complainer = "complainer";

    public static readonly IReadOnlyList<string> All = new[] { Spamtrap, Disposable, Spambot, Complainer };

    internal static string Validate(string type)
    {
        var t = (type ?? "").Trim().ToLowerInvariant();
        if (!All.Contains(t)) throw new ValidationException($"Unknown check \"{type}\". Use one of: {string.Join(", ", All)}", 0, null);
        return t;
    }
}

public sealed class VerifyResult
{
    public string Email { get; set; } = "";
    public string Status { get; set; } = "";
    public string? StatusDescription { get; set; }
    public bool? IsDisposable { get; set; }
    public bool? IsFreeDomain { get; set; }
    public bool? IsRoleBasedDomain { get; set; }
    public bool? IsTypos { get; set; }

    /// <summary>Safe, Avoid or Judgement, derived from <see cref="Status"/>.</summary>
    [JsonIgnore]
    public Band Band => SmtpingClient.GetBand(Status);

    /// <summary>Any other field returned by the API.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CheckResult
{
    public string Email { get; set; } = "";
    public string Check { get; set; } = "";
    public bool Matched { get; set; }
    /// <summary>Why the address matched, for example "recycled-list". Null when not matched.</summary>
    public string? Source { get; set; }
    public long? CreditsCharged { get; set; }
    public long? RemainingCredits { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CheckBatchResult
{
    public string Check { get; set; } = "";
    public int Checked { get; set; }
    public int Matched { get; set; }
    public long? CreditsCharged { get; set; }
    public long? RemainingCredits { get; set; }
    public List<CheckBatchItem> Results { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CheckBatchItem
{
    public string Email { get; set; } = "";
    public bool Matched { get; set; }
    public string? Source { get; set; }
}

public sealed class Credits
{
    public long Remaining { get; set; }
    public string? Plan { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BulkJob
{
    public string JobId { get; set; } = "";
    /// <summary>Queued, Processing, Succeeded, Failed or Cancelled.</summary>
    public string Status { get; set; } = "";
    public int? TotalEmails { get; set; }
    public int? ProcessedEmails { get; set; }
    public string? ErrorMessage { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}
