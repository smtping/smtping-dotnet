using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Smtping;

/// <summary>Bulk verification: submit a list, poll the job, fetch the results.</summary>
public sealed class BulkClient
{
    readonly SmtpingClient _client;

    internal BulkClient(SmtpingClient client) => _client = client;

    /// <summary>Submit a list. Duplicates and malformed addresses are removed first.</summary>
    public async Task<BulkJob> CreateAsync(IEnumerable<string> emails, CancellationToken cancellationToken = default)
    {
        var list = SmtpingClient.Normalize(emails, validOnly: true);
        if (list.Count == 0) throw new ValidationException("No valid email address in the list", 0, null);
        if (list.Count > SmtpingClient.BulkMax) throw new ValidationException($"A bulk job accepts up to {SmtpingClient.BulkMax} addresses", 0, null);
        var job = await _client.SendAsync<BulkJob>(HttpMethod.Post, "/verify/bulk", new { emails = list }, cancellationToken).ConfigureAwait(false);
        job.TotalEmails ??= list.Count;
        return job;
    }

    public async Task<BulkJob> GetAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = await _client.SendAsync<BulkJob>(HttpMethod.Get, "/verify/bulk/" + Uri.EscapeDataString(jobId), null, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(job.JobId)) job.JobId = jobId;
        return job;
    }

    public async Task<IReadOnlyList<VerifyResult>> ResultsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var el = await _client.RequestAsync(HttpMethod.Get, "/verify/bulk/" + Uri.EscapeDataString(jobId) + "/result", null, cancellationToken).ConfigureAwait(false);
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("results", out var inner)) el = inner;
        if (el.ValueKind != JsonValueKind.Array) return Array.Empty<VerifyResult>();
        return el.Deserialize<List<VerifyResult>>(SmtpingClient.Json) ?? new List<VerifyResult>();
    }

    /// <summary>Poll until the job succeeds, then return its results. Throws <see cref="JobFailedException"/> or <see cref="SmtpingTimeoutException"/>.</summary>
    public async Task<IReadOnlyList<VerifyResult>> WaitAsync(string jobId, WaitOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new WaitOptions();
        var deadline = DateTime.UtcNow + options.Timeout;
        var delay = options.Interval;
        for (;;)
        {
            var job = await GetAsync(jobId, cancellationToken).ConfigureAwait(false);
            options.OnProgress?.Invoke(job);
            if (Is(job.Status, "Succeeded")) return await ResultsAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (Is(job.Status, "Failed") || Is(job.Status, "Cancelled"))
            {
                var detail = string.IsNullOrEmpty(job.ErrorMessage) ? "" : ": " + job.ErrorMessage;
                throw new JobFailedException($"Job {jobId} {job.Status?.ToLowerInvariant()}{detail}", job);
            }
            if (DateTime.UtcNow + delay > deadline)
                throw new SmtpingTimeoutException($"Job {jobId} still running after {options.Timeout.TotalSeconds:0} s");
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 30000));
        }
    }

    /// <summary>Create a job and wait for its results in one call.</summary>
    public async Task<IReadOnlyList<VerifyResult>> RunAsync(IEnumerable<string> emails, WaitOptions? options = null, CancellationToken cancellationToken = default)
    {
        var job = await CreateAsync(emails, cancellationToken).ConfigureAwait(false);
        return await WaitAsync(job.JobId, options, cancellationToken).ConfigureAwait(false);
    }

    static bool Is(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
