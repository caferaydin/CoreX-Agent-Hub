namespace CoreX.Hub.Domain.Common;

/// <summary>
/// Raised when a domain invariant is violated. Domain code never throws
/// infrastructure exceptions and never returns partially valid aggregates.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string message) : base(message)
    {
    }
}
