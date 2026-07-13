using System.Net;

namespace Mystral.Services;

public class GlobeApiException : Exception
{
    public GlobeApiException(
        string message,
        HttpStatusCode? statusCode = null,
        string errorCode = "",
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
    }

    public HttpStatusCode? StatusCode { get; }

    public string ErrorCode { get; }

    public TimeSpan? RetryAfter { get; }
}

public sealed class GlobeAuthenticationException : GlobeApiException
{
    public GlobeAuthenticationException(string message, HttpStatusCode? statusCode = null)
        : base(message, statusCode, "not_linked")
    {
    }
}

public sealed class GlobeLinkExpiredException : GlobeApiException
{
    public GlobeLinkExpiredException(string message)
        : base(message, HttpStatusCode.Gone, "link_expired")
    {
    }
}

public sealed class GlobeNotLinkedException : InvalidOperationException
{
    public GlobeNotLinkedException()
        : base("A Globe account is not linked.")
    {
    }
}
