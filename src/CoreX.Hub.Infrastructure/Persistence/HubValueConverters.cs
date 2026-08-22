using System.Text.Json;
using System.Text.Json.Serialization;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CoreX.Hub.Infrastructure.Persistence;

/// <summary>
/// Value conversions for the domain value objects and for the few collections the
/// architecture stores as JSON documents rather than tables (architecture M.2).
/// Explicit DTOs are used for JSON so the on-disk shape never depends on serializer
/// defaults for records or read-only interfaces.
/// </summary>
internal static class HubValueConverters
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static readonly ValueConverter<Slug, string> Slug =
        new(v => v.Value, v => Domain.Common.Slug.Parse(v));

    public static readonly ValueConverter<TaskKey, string> TaskKey =
        new(v => v.Value, v => Domain.Tasks.TaskKey.Parse(v));

    public static readonly ValueConverter<List<string>, string> StringList =
        new(v => Serialize(v), v => DeserializeStringList(v));

    public static readonly ValueComparer<List<string>> StringListComparer =
        new(
            (a, b) => a != null && b != null && a.SequenceEqual(b),
            v => v.Aggregate(17, (acc, s) => (acc * 31) + (s == null ? 0 : s.GetHashCode())),
            v => v.ToList());

    public static readonly ValueConverter<List<VerificationStep>, string> VerificationSteps =
        new(v => SerializeSteps(v), v => DeserializeSteps(v));

    public static readonly ValueComparer<List<VerificationStep>> VerificationStepsComparer =
        new(
            (a, b) => a != null && b != null && a.SequenceEqual(b),
            v => v.Aggregate(17, (acc, s) => (acc * 31) + (s == null ? 0 : s.GetHashCode())),
            v => v.ToList());

    public static readonly ValueConverter<List<HumanCriterionVerdictRecord>, string> HumanVerdicts =
        new(v => SerializeVerdicts(v), v => DeserializeVerdicts(v));

    public static readonly ValueComparer<List<HumanCriterionVerdictRecord>> HumanVerdictsComparer =
        new(
            (a, b) => a != null && b != null && a.SequenceEqual(b),
            v => v.Aggregate(17, (acc, s) => (acc * 31) + (s == null ? 0 : s.GetHashCode())),
            v => v.ToList());

    private static string Serialize(List<string> value) =>
        JsonSerializer.Serialize(value ?? [], JsonOptions);

    private static List<string> DeserializeStringList(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : JsonSerializer.Deserialize<List<string>>(value, JsonOptions) ?? [];

    private static string SerializeSteps(List<VerificationStep> value) =>
        JsonSerializer.Serialize(
            (value ?? []).Select(s => new StepDto(
                s.Name,
                s.Executable,
                [.. s.Args],
                s.TimeoutSeconds,
                s.Required,
                s.ResultParser.ToString(),
                s.ResultPath)).ToList(),
            JsonOptions);

    private static List<VerificationStep> DeserializeSteps(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var dtos = JsonSerializer.Deserialize<List<StepDto>>(value, JsonOptions) ?? [];
        return [.. dtos.Select(d => new VerificationStep(
            d.Name,
            d.Executable,
            d.Args,
            d.TimeoutSeconds,
            d.Required,
            Enum.Parse<VerificationResultParser>(d.ResultParser, ignoreCase: true),
            d.ResultPath))];
    }

    private static string SerializeVerdicts(List<HumanCriterionVerdictRecord> value) =>
        JsonSerializer.Serialize(
            (value ?? []).Select(v => new VerdictDto(v.CriterionId, v.Verdict.ToString(), v.Note)).ToList(),
            JsonOptions);

    private static List<HumanCriterionVerdictRecord> DeserializeVerdicts(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var dtos = JsonSerializer.Deserialize<List<VerdictDto>>(value, JsonOptions) ?? [];
        return [.. dtos.Select(d => new HumanCriterionVerdictRecord(
            d.CriterionId,
            Enum.Parse<HumanCriterionVerdict>(d.Verdict, ignoreCase: true),
            d.Note))];
    }

    private sealed record StepDto(
        string Name,
        string Executable,
        string[] Args,
        int TimeoutSeconds,
        bool Required,
        string ResultParser,
        string? ResultPath = null);

    private sealed record VerdictDto(Guid CriterionId, string Verdict, string? Note);
}
