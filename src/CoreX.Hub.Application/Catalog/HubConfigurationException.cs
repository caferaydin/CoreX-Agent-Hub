namespace CoreX.Hub.Application.Catalog;

/// <summary>Raised when <c>hub.config.yaml</c> cannot be turned into a valid catalog.</summary>
public sealed class HubConfigurationException : Exception
{
    public HubConfigurationException(string message) : base(message)
    {
    }

    public HubConfigurationException(string message, Exception inner) : base(message, inner)
    {
    }
}
