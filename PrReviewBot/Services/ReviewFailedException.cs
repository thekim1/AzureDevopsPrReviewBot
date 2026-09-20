namespace PrReviewBot.Services;

// Raised when a provider came back but its answer could not be turned into a
// review. This must never be swallowed into an empty comment list: an empty
// list means "the model read the diff and found nothing", and showing that for
// a failed call tells you a PR is clean when nobody actually reviewed it.
public sealed class ReviewFailedException : Exception
{
    public ReviewFailedException(string message, string? rawResponse = null)
        : base(message)
    {
        RawResponse = rawResponse;
    }

    public ReviewFailedException(string message, Exception inner, string? rawResponse = null)
        : base(message, inner)
    {
        RawResponse = rawResponse;
    }

    public ReviewFailedException()
    {
    }

    public ReviewFailedException(string message) : base(message)
    {
    }

    public ReviewFailedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    // The provider's unparsed reply, written to disk so the failure can be
    // diagnosed without re-running (and re-paying for) the request.
    public string? RawResponse { get; }
}
