using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CoreX.Hub.Infrastructure.Configuration;

/// <summary>Reads <c>hub.config.yaml</c> into the Application configuration records.</summary>
public sealed class YamlHubConfigReader : IHubConfigReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public HubConfig Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new HubConfigurationException($"Configuration file not found: {path}");
        }

        var yaml = File.ReadAllText(path);

        try
        {
            return Deserializer.Deserialize<HubConfig>(yaml)
                ?? throw new HubConfigurationException($"Configuration file is empty: {path}");
        }
        catch (YamlException ex)
        {
            throw new HubConfigurationException($"Failed to parse {path}: {ex.Message}", ex);
        }
    }
}
