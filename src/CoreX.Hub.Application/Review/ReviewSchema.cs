using System.Text.Json;
using System.Text.Json.Serialization;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Review;

/// <summary>
/// The one place the reviewer's output contract is written down.
///
/// It exists once because it is three things at the same time: what the provider is told to
/// produce, what the Hub deserialises, and what the tests assert. Writing the same shape in
/// a schema literal, a DTO and a fixture would let those three drift, and the drift would
/// only ever be discovered as a review that could not be read.
///
/// Nothing here is a verdict. There is no score, no approval, no risk number and no
/// "should merge": the reviewer reports evidence and the Hub derives the outcome from it
/// (J.4). A field that let a provider state the answer would make the gate advisory.
/// </summary>
public static class ReviewSchema
{
    /// <summary>Frozen J.3 bound: a title is a headline, not a paragraph.</summary>
    public const int TitleMaxLength = 80;

    /// <summary>Frozen J.3 bound, and the same number the evidence summary is stored under.</summary>
    public const int SummaryMaxLength = 500;

    /// <summary>
    /// The severity vocabulary, taken from the domain enum rather than restated.
    ///
    /// Restating it would create a second severity authority: a list here and the enum
    /// there, agreeing today and drifting the first time one of them changed. Deriving it
    /// means a severity the domain knows is a severity the schema offers, by construction,
    /// and it keeps every judgement about what a severity means in the one place that is
    /// allowed to make one.
    /// </summary>
    public static IReadOnlyList<string> Severities { get; } = System.Enum.GetNames<FindingSeverity>();

    public static IReadOnlyList<string> Categories { get; } =
        ["correctness", "security", "architecture", "performance", "test-coverage", "convention"];

    public static IReadOnlyList<string> Confidences { get; } = ["high", "medium", "low"];

    /// <summary>
    /// The JSON Schema handed to the provider, built from the vocabularies above.
    ///
    /// Built rather than pasted: a literal would be a second copy of the enums that the
    /// compiler could not check against the first, and the first thing to rot would be a
    /// value the domain accepts and the schema does not.
    /// </summary>
    public static string Json { get; } = Build();

    /// <summary>
    /// Reads a provider structured document, or says why it is not one.
    ///
    /// Strict on purpose. Unknown severities, categories and confidences are refused rather
    /// than defaulted, because a review whose severity nobody recognised is not a review
    /// with a mild severity - it is a review that cannot be judged. The same applies to a
    /// missing field: there is no sensible substitute for a rationale nobody wrote.
    /// </summary>
    public static ReviewDocumentResult Read(string? structuredOutput)
    {
        if (string.IsNullOrWhiteSpace(structuredOutput))
        {
            return ReviewDocumentResult.Invalid("the provider returned no structured output");
        }

        ReviewDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ReviewDocument>(structuredOutput, Options);
        }
        catch (JsonException ex)
        {
            return ReviewDocumentResult.Invalid("the structured output is not readable json: " + ex.Message);
        }

        if (document is null)
        {
            return ReviewDocumentResult.Invalid("the structured output was null");
        }

        if (document.Summary is null)
        {
            return ReviewDocumentResult.Invalid("the structured output carries no summary");
        }

        if (document.Summary.Length > SummaryMaxLength)
        {
            return ReviewDocumentResult.Invalid(
                $"the summary is {document.Summary.Length} characters, over the {SummaryMaxLength} allowed");
        }

        if (document.Findings is null)
        {
            return ReviewDocumentResult.Invalid("the structured output carries no findings array");
        }

        for (var i = 0; i < document.Findings.Count; i++)
        {
            if (Validate(document.Findings[i], i) is { } problem)
            {
                return ReviewDocumentResult.Invalid(problem);
            }
        }

        return ReviewDocumentResult.Valid(document);
    }

    /// <summary>
    /// Maps one vocabulary word, or nothing. Never a default.
    ///
    /// Case sensitive on purpose: the schema states the exact words, and a provider that
    /// returned "critical" did not answer the question it was asked. Accepting it would mean
    /// deciding on its behalf which severity it meant.
    /// </summary>
    public static FindingSeverity? Severity(string? value) =>
        value is not null && System.Enum.TryParse<FindingSeverity>(value, ignoreCase: false, out var severity)
        && Severities.Contains(value, StringComparer.Ordinal)
            ? severity
            : null;

    public static FindingCategory? Category(string? value) => value switch
    {
        "correctness" => FindingCategory.Correctness,
        "security" => FindingCategory.Security,
        "architecture" => FindingCategory.Architecture,
        "performance" => FindingCategory.Performance,
        "test-coverage" => FindingCategory.TestCoverage,
        "convention" => FindingCategory.Convention,
        _ => null,
    };

    public static FindingConfidence? Confidence(string? value) => value switch
    {
        "high" => FindingConfidence.High,
        "medium" => FindingConfidence.Medium,
        "low" => FindingConfidence.Low,
        _ => null,
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    private static string? Validate(ReviewFindingDocument finding, int index)
    {
        var at = $"finding {index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        if (Severity(finding.Severity) is null)
        {
            return $"{at} has severity '{finding.Severity}', which is not one of {Join(Severities)}";
        }

        if (Category(finding.Category) is null)
        {
            return $"{at} has category '{finding.Category}', which is not one of {Join(Categories)}";
        }

        if (Confidence(finding.Confidence) is null)
        {
            return $"{at} has confidence '{finding.Confidence}', which is not one of {Join(Confidences)}";
        }

        if (string.IsNullOrWhiteSpace(finding.Title))
        {
            return $"{at} has no title";
        }

        if (finding.Title.Length > TitleMaxLength)
        {
            return $"{at} has a title of {finding.Title.Length} characters, over the {TitleMaxLength} allowed";
        }

        if (string.IsNullOrWhiteSpace(finding.Rationale))
        {
            return $"{at} has no rationale";
        }

        if (string.IsNullOrWhiteSpace(finding.File))
        {
            return $"{at} names no file";
        }

        // A line number is an anchor into a file. Zero and negatives anchor nothing, and
        // clamping them silently would turn an unreadable finding into a plausible one.
        if (finding.Line is not { } line || line < 1)
        {
            return $"{at} has line '{finding.Line}', which does not anchor anywhere";
        }

        if (finding.OutOfScope is null)
        {
            return $"{at} does not say whether it is out of scope";
        }

        return null;
    }

    private static string Join(IReadOnlyList<string> values) => string.Join(", ", values);

    private static string Build()
    {
        static string Enum(IReadOnlyList<string> values) =>
            "{\"enum\":[" + string.Join(',', values.Select(v => "\"" + v + "\"")) + "]}";

        return "{"
               + "\"type\":\"object\",\"additionalProperties\":false,"
               + "\"required\":[\"findings\",\"summary\"],"
               + "\"properties\":{"
               + "\"findings\":{\"type\":\"array\",\"items\":{"
               + "\"type\":\"object\",\"additionalProperties\":false,"
               + "\"required\":[\"severity\",\"category\",\"file\",\"line\",\"title\",\"rationale\","
               + "\"suggestedFix\",\"confidence\",\"outOfScope\"],"
               + "\"properties\":{"
               + "\"severity\":" + Enum(Severities) + ","
               + "\"category\":" + Enum(Categories) + ","
               + "\"file\":{\"type\":\"string\"},"
               + "\"line\":{\"type\":\"integer\",\"minimum\":1},"
               + "\"title\":{\"type\":\"string\",\"maxLength\":"
               + TitleMaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + "},"
               + "\"rationale\":{\"type\":\"string\"},"
               + "\"suggestedFix\":{\"type\":[\"string\",\"null\"]},"
               + "\"confidence\":" + Enum(Confidences) + ","
               + "\"outOfScope\":{\"type\":\"boolean\"}}}},"
               + "\"summary\":{\"type\":\"string\",\"maxLength\":"
               + SummaryMaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}";
    }
}

/// <summary>One finding exactly as the provider wrote it. Not yet Hub truth.</summary>
public sealed record ReviewFindingDocument
{
    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("category")]
    public string? Category { get; init; }

    [JsonPropertyName("file")]
    public string? File { get; init; }

    [JsonPropertyName("line")]
    public int? Line { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("rationale")]
    public string? Rationale { get; init; }

    [JsonPropertyName("suggestedFix")]
    public string? SuggestedFix { get; init; }

    [JsonPropertyName("confidence")]
    public string? Confidence { get; init; }

    /// <summary>
    /// What the reviewer claimed about scope. Evidence, never authority: the Hub decides
    /// scope from the paths the execution actually changed (J.4), because a reviewer that
    /// could set this would be able to exempt its own blocking finding.
    /// </summary>
    [JsonPropertyName("outOfScope")]
    public bool? OutOfScope { get; init; }
}

public sealed record ReviewDocument
{
    [JsonPropertyName("findings")]
    public IReadOnlyList<ReviewFindingDocument>? Findings { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }
}

/// <summary>A readable review document, or the reason there is not one.</summary>
public sealed record ReviewDocumentResult
{
    private ReviewDocumentResult()
    {
    }

    public ReviewDocument? Document { get; private init; }

    public string? Problem { get; private init; }

    public bool IsValid => Document is not null && Problem is null;

    public static ReviewDocumentResult Valid(ReviewDocument document) => new() { Document = document };

    public static ReviewDocumentResult Invalid(string problem) => new() { Problem = problem };
}
