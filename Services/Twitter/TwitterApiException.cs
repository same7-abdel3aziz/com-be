using System.Net;

namespace CompetitionManagementSystem.Services.Twitter;

public sealed class TwitterApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? RetryAfter { get; }

    public TwitterApiException(string message, HttpStatusCode statusCode, string? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public bool IsRateLimited => StatusCode == HttpStatusCode.TooManyRequests;
    public bool IsPaymentRequired => StatusCode == HttpStatusCode.PaymentRequired;
}