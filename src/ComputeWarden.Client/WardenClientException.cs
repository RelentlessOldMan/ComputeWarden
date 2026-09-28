namespace ComputeWarden.Client;

/// <summary>
/// Thrown when the daemon returns a protocol-level error (<c>ok:false</c>) or the response
/// cannot be understood. Connection failures surface as the underlying IO/timeout exceptions.
/// </summary>
public sealed class WardenClientException : Exception
{
    public WardenClientException(string message) : base(message) { }
    public WardenClientException(string message, Exception inner) : base(message, inner) { }
}
