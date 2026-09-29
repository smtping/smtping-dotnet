using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Smtping;

/// <summary>Client for the SMTPing email verification API.</summary>
public sealed class SmtpingClient : IDisposable
{
    public const string DefaultBaseUrl = "https://api.smtping.com/api/v1";
    public const int BulkMax = 100_000;
    public const int BatchMax = 1_000;

    /// <summary>Package version, sent in the User-Agent header.</summary>
    public static readonly string Version =
        typeof(SmtpingClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    static readonly Regex EmailRe = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    static readonly string[] SafeStatuses = { "valid", "alias" };
    static readonly string[] AvoidStatuses = { "invalid", "spamtrap", "disposable", "blacklisted", "complainer", "spambot", "inbox_full" };
    static readonly Random Jitter = new();

    readonly HttpClient _http;
    readonly bool _ownsHttp;
    readonly string _apiKey;

    public string BaseUrl { get; }
    public TimeSpan Timeout { get; }
    public int MaxRetries { get; }
    public string UserAgent { get; }

    /// <summary>Bulk verification jobs, up to 100,000 addresses each.</summary>
    public BulkClient Bulk { get; }

    public SmtpingClient(string apiKey) : this(new SmtpingOptions { ApiKey = apiKey }) { }

    public SmtpingClient(SmtpingOptions? options = null)
    {
        options ??= new SmtpingOptions();
        _apiKey = FirstNonEmpty(options.ApiKey, Environment.GetEnvironmentVariable("SMTPING_API_KEY"));
        if (_apiKey.Length == 0)
            throw new AuthenticationException("Missing API key. Pass it to the constructor or set SMTPING_API_KEY.", 0, null);
        BaseUrl = FirstNonEmpty(options.BaseUrl, Environment.GetEnvironmentVariable("SMTPING_BASE_URL"), DefaultBaseUrl).TrimEnd('/');
        Timeout = options.Timeout ?? TimeSpan.FromSeconds(60);
        MaxRetries = options.MaxRetries ?? 3;
        UserAgent = FirstNonEmpty(options.UserAgent, "smtping-dotnet/" + Version);
        _ownsHttp = options.HttpClient == null;
        _http = options.HttpClient ?? new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        Bulk = new BulkClient(this);
    }

    /// <summary>Verify one address. The result carries a ready-made <see cref="Band"/>.</summary>
    public Task<VerifyResult> VerifyAsync(string email, CancellationToken cancellationToken = default)
    {
        var e = (email ?? "").Trim();
        if (e.Length == 0) throw new ValidationException("Email is required", 0, null);
        return SendAsync<VerifyResult>(HttpMethod.Post, "/verify/single", new { email = e }, cancellationToken);
    }

    /// <summary>Verify a small list with parallel single calls. Failed addresses come back with status "error".</summary>
    public async Task<IReadOnlyList<VerifyResult>> VerifyManyAsync(IEnumerable<string> emails, int concurrency = 5, CancellationToken cancellationToken = default)
    {
        var list = Normalize(emails, validOnly: false);
        var output = new VerifyResult[list.Count];
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = list.Select(async (email, i) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { output[i] = await VerifyAsync(email, cancellationToken).ConfigureAwait(false); }
            catch (SmtpingException ex) { output[i] = new VerifyResult { Email = email, Status = "error", StatusDescription = ex.Message }; }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return output;
    }

    /// <summary>Look one address up in a threat list. Use the <see cref="CheckType"/> constants.</summary>
    public async Task<CheckResult> CheckAsync(string type, string email, CancellationToken cancellationToken = default)
    {
        var t = CheckType.Validate(type);
        var e = (email ?? "").Trim();
        var r = await SendAsync<CheckResult>(HttpMethod.Post, "/checks/" + t, new { email = e }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(r.Email)) r.Email = e;
        if (string.IsNullOrEmpty(r.Check)) r.Check = t;
        return r;
    }

    /// <summary>Look up to 1,000 addresses up in a threat list in one call.</summary>
    public Task<CheckBatchResult> CheckBatchAsync(string type, IEnumerable<string> emails, CancellationToken cancellationToken = default)
    {
        var t = CheckType.Validate(type);
        var list = Normalize(emails, validOnly: true);
        if (list.Count == 0) throw new ValidationException("No valid email address in the list", 0, null);
        if (list.Count > BatchMax) throw new ValidationException($"A batch check accepts up to {BatchMax} addresses. Use a job above that.", 0, null);
        return SendAsync<CheckBatchResult>(HttpMethod.Post, "/checks/" + t + "/batch", new { emails = list }, cancellationToken);
    }

    /// <summary>Remaining credit balance.</summary>
    public Task<Credits> CreditsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<Credits>(HttpMethod.Get, "/credits", null, cancellationToken);

    /// <summary>Routing band for a verification status: valid and alias are Safe, hard failures and threats are Avoid, the rest is Judgement.</summary>
    public static Band GetBand(string? status)
    {
        if (status == null) return Band.Judgement;
        if (SafeStatuses.Contains(status)) return Band.Safe;
        if (AvoidStatuses.Contains(status)) return Band.Avoid;
        return Band.Judgement;
    }

    /// <summary>Basic syntax check, the same one the SDK applies before a bulk job.</summary>
    public static bool IsEmail(string? value) => value != null && EmailRe.IsMatch(value.Trim());

    /// <summary>Low-level call to any endpoint. Retries network errors, 429 and 5xx.</summary>
    public async Task<JsonElement> RequestAsync(HttpMethod method, string path, object? body = null, CancellationToken cancellationToken = default)
    {
        var payload = body == null ? null : JsonSerializer.Serialize(body, Json);
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, BaseUrl + path);
            req.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            if (payload != null) req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(Timeout);
            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && (ex is HttpRequestException || ex is OperationCanceledException))
            {
                if (attempt < MaxRetries) { await Task.Delay(Backoff(attempt + 1), cancellationToken).ConfigureAwait(false); continue; }
                if (ex is OperationCanceledException) throw new SmtpingTimeoutException($"Request timed out after {Timeout.TotalMilliseconds:0} ms");
                throw new SmtpingException("Network error: " + ex.Message, 0, null, ex);
            }

            using (res)
            {
                var text = res.Content == null ? "" : await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                var status = (int)res.StatusCode;
                if (res.IsSuccessStatusCode) return Parse(text);
                if ((status == 429 || status >= 500) && attempt < MaxRetries)
                {
                    var wait = res.Headers.RetryAfter?.Delta;
                    await Task.Delay(wait > TimeSpan.Zero ? wait.Value : Backoff(attempt + 1), cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw ErrorFor(status, text);
            }
        }
    }

    internal async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var el = await RequestAsync(method, path, body, ct).ConfigureAwait(false);
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            throw new SmtpingException("Empty response from the SMTPing API", 0, null);
        return el.Deserialize<T>(Json)!;
    }

    internal static List<string> Normalize(IEnumerable<string> emails, bool validOnly)
    {
        if (emails == null) throw new ArgumentNullException(nameof(emails));
        return emails
            .Select(e => (e ?? "").Trim().ToLowerInvariant())
            .Where(e => e.Length > 0 && (!validOnly || IsEmail(e)))
            .Distinct()
            .ToList();
    }

    static JsonElement Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) text = "null";
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { raw = text }));
            return doc.RootElement.Clone();
        }
    }

    static SmtpingException ErrorFor(int status, string body)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String) message = e.GetString();
                else if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString();
            }
        }
        catch (JsonException) { }
        message ??= $"SMTPing API returned HTTP {status}";
        return status switch
        {
            401 or 403 => new AuthenticationException(message, status, body),
            402 => new InsufficientCreditsException(message, status, body),
            429 => new RateLimitException(message, status, body),
            400 or 422 => new ValidationException(message, status, body),
            _ => new SmtpingException(message, status, body),
        };
    }

    static TimeSpan Backoff(int attempt)
    {
        int jitter;
        lock (Jitter) jitter = Jitter.Next(250);
        var ms = Math.Min(1000 * Math.Pow(2, attempt - 1), 15000) + jitter;
        return TimeSpan.FromMilliseconds(ms);
    }

    static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
