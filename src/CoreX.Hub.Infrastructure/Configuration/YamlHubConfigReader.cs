using CoreX.Hub.Application.Catalog;
using CoreX.Hub.Application.Configuration;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CoreX.Hub.Infrastructure.Configuration;

/// <summary>
/// Reads <c>hub.config.yaml</c> into the Application configuration records.
///
/// The deserialiser is deliberately <b>fail closed</b>: it does NOT ignore unmatched
/// properties. A misspelled key must never fall back to a default CLR value, because
/// configuration carries security and execution sensitive fields such as
/// <c>isolationLevel</c>, <c>requiresHardIsolation</c>, organization filesystem roots,
/// repository paths and verification profiles.
///
/// Concretely, a typo like <c>requiresHardIsolaton: true</c> must not silently become
/// <c>RequiresHardIsolation = false</c> and produce an apparently Active organization,
/// which would defeat the ADR-0017 hard isolation gate before domain validation ever
/// sees the intended value.
///
/// Duplicate mapping keys are rejected for the same reason. YAML would otherwise let
/// the last occurrence silently shadow the first, so
/// <c>requiresHardIsolation: true</c> followed by <c>requiresHardIsolation: false</c>
/// would quietly resolve to false and produce an Active organization. Effective
/// configuration must never depend on which duplicate happened to come last.
///
/// This is not a schema validation framework and not a custom parser: both behaviours
/// are the YAML stack's own strict modes.
/// </summary>
public sealed class YamlHubConfigReader : IHubConfigReader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithDuplicateKeyChecking()
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
            throw new HubConfigurationException(Describe(path, ex), ex);
        }
    }

    /// <summary>
    /// Produces a single readable line naming the file, the position and the offending
    /// key, so the operator never has to read a stack trace to fix a typo.
    /// The offending property name lives in the innermost exception when the error is
    /// nested inside a sequence, so the whole chain is folded into the message.
    /// </summary>
    private static string Describe(string path, YamlException exception)
    {
        var messages = new List<string>();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message.Trim();
            if (message.Length > 0 && !messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }
        }

        var position = exception.Start.Line > 0
            ? $" at line {exception.Start.Line}, column {exception.Start.Column}"
            : string.Empty;

        return $"Invalid configuration {path}{position}: {string.Join(" -> ", messages)}";
    }
}
