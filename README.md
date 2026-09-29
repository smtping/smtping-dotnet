# SMTPing Email Verifier for .NET

Official .NET SDK for the [SMTPing](https://smtping.com) email verification API.

- .NET 8+ and .NET Standard 2.0 (.NET Framework 4.6.2+, Unity, Xamarin)
- Async methods with `CancellationToken`, typed results and exceptions
- Automatic retries on rate limits (429) and server errors (5xx)
- Bulk jobs up to 100,000 addresses, with polling built in

## Install

```bash
dotnet add package SMTPing.EmailVerifier
```

Create an API key in the [SMTPing dashboard](https://app.smtping.com). Pass it to the client or set `SMTPING_API_KEY`.

## Verify one address

```csharp
using Smtping;

using var smtping = new SmtpingClient(); // reads SMTPING_API_KEY, or new SmtpingClient("sk_live_...")

var r = await smtping.VerifyAsync("jane@example.com");
Console.WriteLine($"{r.Status} {r.Band}"); // valid Safe
```

Every result carries a `Band` for simple routing:

| Band | statuses | action |
| --- | --- | --- |
| `Safe` | valid, alias | send |
| `Avoid` | invalid, spamtrap, disposable, blacklisted, complainer, spambot, inbox_full | remove |
| `Judgement` | catch_all, unknown, role and others | your call |

## Verify a list

Small lists with parallel single calls:

```csharp
var rows = await smtping.VerifyManyAsync(new[] { "a@example.com", "b@example.com" }, concurrency: 5);
```

Large lists as one bulk job:

```csharp
var job = await smtping.Bulk.CreateAsync(emails);
var rows = await smtping.Bulk.WaitAsync(job.JobId, new WaitOptions
{
    OnProgress = s => Console.WriteLine(s.ProcessedEmails),
});

// or in one call
var all = await smtping.Bulk.RunAsync(emails);
var sendable = all.Where(x => x.Band == Band.Safe).ToList();
```

Check a job later with `Bulk.GetAsync(jobId)` and `Bulk.ResultsAsync(jobId)`.

## Threat list checks

```csharp
var trap = await smtping.CheckAsync(CheckType.Spamtrap, "jane@example.com");
Console.WriteLine($"{trap.Matched} {trap.Source}");
// also: CheckType.Disposable, CheckType.Spambot, CheckType.Complainer

// up to 1,000 addresses in one call
var batch = await smtping.CheckBatchAsync(CheckType.Disposable, emails);
```

## Credits

```csharp
var credits = await smtping.CreditsAsync();
Console.WriteLine(credits.Remaining);
```

## ASP.NET Core

Register one client for the whole app, or pass your own `HttpClient`:

```csharp
builder.Services.AddSingleton(new SmtpingClient(builder.Configuration["Smtping:ApiKey"]!));

// with IHttpClientFactory
builder.Services.AddHttpClient("smtping");
builder.Services.AddSingleton(sp => new SmtpingClient(new SmtpingOptions
{
    ApiKey = builder.Configuration["Smtping:ApiKey"],
    HttpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("smtping"),
}));
```

## Errors

```csharp
try
{
    await smtping.VerifyAsync("jane@example.com");
}
catch (InsufficientCreditsException)
{
    // top up
}
catch (SmtpingException e)
{
    Console.WriteLine($"{e.Status} {e.Message}");
}
```

Classes: `SmtpingException` (base, with `Status` and `Body`), `AuthenticationException`, `InsufficientCreditsException`, `RateLimitException`, `ValidationException`, `JobFailedException`, `SmtpingTimeoutException`.

## Options

| option | default | |
| --- | --- | --- |
| `ApiKey` | `SMTPING_API_KEY` | required |
| `BaseUrl` | `https://api.smtping.com/api/v1` | |
| `Timeout` | 60 seconds | per request |
| `MaxRetries` | `3` | network errors, 429, 5xx |
| `HttpClient` | new instance | not disposed by the SDK when you pass one |

## Command line

A CLI ships with the Node package: `npx @smtping/sdk verify jane@example.com`. See the [docs](https://smtping.com/docs#sdks).

## Links

- [API documentation](https://smtping.com/docs)
- [Pricing](https://smtping.com/pricing)
- Support: support@smtping.com

MIT License
