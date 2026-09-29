using System;

namespace Smtping;

/// <summary>Base class for every SMTPing error. <see cref="Status"/> is 0 when no HTTP response was received.</summary>
public class SmtpingException : Exception
{
    public int Status { get; }
    /// <summary>Raw response body, when there is one.</summary>
    public string? Body { get; }

    public SmtpingException(string message, int status, string? body, Exception? inner = null) : base(message, inner)
    {
        Status = status;
        Body = body;
    }
}

/// <summary>Missing, invalid or revoked API key (401, 403).</summary>
public class AuthenticationException : SmtpingException
{
    public AuthenticationException(string message, int status, string? body) : base(message, status, body) { }
}

/// <summary>Not enough credits (402).</summary>
public class InsufficientCreditsException : SmtpingException
{
    public InsufficientCreditsException(string message, int status, string? body) : base(message, status, body) { }
}

/// <summary>Rate limit still exceeded after the automatic retries (429).</summary>
public class RateLimitException : SmtpingException
{
    public RateLimitException(string message, int status, string? body) : base(message, status, body) { }
}

/// <summary>Invalid input, rejected locally or by the API (400, 422).</summary>
public class ValidationException : SmtpingException
{
    public ValidationException(string message, int status, string? body) : base(message, status, body) { }
}

/// <summary>A bulk job ended as Failed or Cancelled.</summary>
public class JobFailedException : SmtpingException
{
    public BulkJob Job { get; }
    public JobFailedException(string message, BulkJob job) : base(message, 0, null) => Job = job;
}

/// <summary>A request or a bulk wait took longer than allowed.</summary>
public class SmtpingTimeoutException : SmtpingException
{
    public SmtpingTimeoutException(string message) : base(message, 0, null) { }
}
