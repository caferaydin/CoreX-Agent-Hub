namespace CoreX.Hub.Application.Configuration;

/// <summary>Reads and parses <c>hub.config.yaml</c>. Implemented in Infrastructure.</summary>
public interface IHubConfigReader
{
    HubConfig Read(string path);
}
