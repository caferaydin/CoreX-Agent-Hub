using System.Text.Json;
using CoreX.Hub.Application.Review;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Application.Tests;

/// <summary>
/// The reviewer output contract, read strictly.
///
/// Every refusal here is a way a review could be read leniently and come out wrong: a
/// severity nobody recognises quietly becoming Info, a missing scope flag defaulting to in
/// scope, a line number of zero anchoring nowhere. None of those is a mild review; each of
/// them is a review that cannot be judged, and the schema says so rather than guessing.
/// </summary>
public sealed class ReviewSchemaTests
{
    [Fact]
    public void The_severity_vocabulary_is_the_domain_enum_and_not_a_second_list()
    {
        Assert.Equal(Enum.GetNames<FindingSeverity>(), ReviewSchema.Severities);
    }

    [Fact]
    public void The_schema_is_closed_so_a_provider_cannot_add_a_field_of_its_own()
    {
        using var document = JsonDocument.Parse(ReviewSchema.Json);
        var root = document.RootElement;

        Assert.False(root.GetProperty("additionalProperties").GetBoolean());

        var finding = root.GetProperty("properties").GetProperty("findings").GetProperty("items");

        Assert.False(finding.GetProperty("additionalProperties").GetBoolean());

        // Every field is required. An optional field is one a provider can omit and the Hub
        // has to invent an answer for.
        var required = finding.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(
            ["severity", "category", "file", "line", "title", "rationale", "suggestedFix", "confidence", "outOfScope"],
            required);
    }

    [Fact]
    public void A_line_number_below_one_is_not_offered_by_the_schema()
    {
        using var document = JsonDocument.Parse(ReviewSchema.Json);

        var line = document.RootElement
            .GetProperty("properties").GetProperty("findings").GetProperty("items")
            .GetProperty("properties").GetProperty("line");

        Assert.Equal(1, line.GetProperty("minimum").GetInt32());
    }

    [Fact]
    public void The_schema_offers_no_field_a_provider_could_state_a_verdict_in()
    {
        using var document = JsonDocument.Parse(ReviewSchema.Json);

        var json = ReviewSchema.Json;

        foreach (var forbidden in new[] { "verdict", "blocking", "approved", "score", "risk", "shouldMerge" })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(
            ["findings", "summary"],
            document.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData(null, "no structured output")]
    [InlineData("", "no structured output")]
    [InlineData("   ", "no structured output")]
    [InlineData("{ not json", "not readable json")]
    [InlineData("null", "was null")]
    [InlineData("{\"findings\":[]}", "no summary")]
    [InlineData("{\"summary\":\"s\"}", "no findings array")]
    public void A_document_that_is_not_one_is_refused_with_a_reason(string? input, string expected)
    {
        var result = ReviewSchema.Read(input);

        Assert.False(result.IsValid);
        Assert.Contains(expected, result.Problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("CRITICAL")]
    [InlineData("Blocker")]
    [InlineData("0")]
    [InlineData("")]
    public void A_severity_the_hub_does_not_recognise_is_refused_rather_than_defaulted(string severity)
    {
        var result = ReviewSchema.Read(
            ReviewDocuments.Document("s", ReviewDocuments.Finding(severity: severity)));

        Assert.False(result.IsValid);
        Assert.Contains("severity", result.Problem!, StringComparison.Ordinal);
        Assert.Null(ReviewSchema.Severity(severity));
    }

    [Theory]
    [InlineData("Correctness")]
    [InlineData("testcoverage")]
    [InlineData("style")]
    public void A_category_the_hub_does_not_recognise_is_refused(string category)
    {
        var result = ReviewSchema.Read(
            ReviewDocuments.Document("s", ReviewDocuments.Finding(category: category)));

        Assert.False(result.IsValid);
        Assert.Contains("category", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_confidence_the_hub_does_not_recognise_is_refused()
    {
        var result = ReviewSchema.Read(
            ReviewDocuments.Document("s", ReviewDocuments.Finding(confidence: "High")));

        Assert.False(result.IsValid);
        Assert.Contains("confidence", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_title_longer_than_the_frozen_bound_is_refused()
    {
        var result = ReviewSchema.Read(ReviewDocuments.Document(
            "s", ReviewDocuments.Finding(title: new string('t', ReviewSchema.TitleMaxLength + 1))));

        Assert.False(result.IsValid);
        Assert.Contains("title", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_summary_longer_than_the_frozen_bound_is_refused()
    {
        var result = ReviewSchema.Read(
            ReviewDocuments.Document(new string('s', ReviewSchema.SummaryMaxLength + 1)));

        Assert.False(result.IsValid);
        Assert.Contains("summary", result.Problem!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_line_that_anchors_nowhere_is_refused_rather_than_clamped(int line)
    {
        var result = ReviewSchema.Read(
            ReviewDocuments.Document("s", ReviewDocuments.Finding(line: line)));

        Assert.False(result.IsValid);
        Assert.Contains("anchor", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finding_that_does_not_say_whether_it_is_in_scope_is_refused()
    {
        var json = JsonSerializer.Serialize(new
        {
            findings = new[]
            {
                new
                {
                    severity = "Major",
                    category = "correctness",
                    file = "src/One.cs",
                    line = 3,
                    title = "t",
                    rationale = "r",
                    suggestedFix = (string?)null,
                    confidence = "high",
                },
            },
            summary = "s",
        });

        var result = ReviewSchema.Read(json);

        Assert.False(result.IsValid);
        Assert.Contains("out of scope", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finding_with_no_rationale_is_refused_because_there_is_no_substitute_for_one()
    {
        var result = ReviewSchema.Read(
            ReviewDocuments.Document("s", ReviewDocuments.Finding(rationale: "  ")));

        Assert.False(result.IsValid);
        Assert.Contains("rationale", result.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_document_reads_back_exactly_as_it_was_written()
    {
        var result = ReviewSchema.Read(ReviewDocuments.Document(
            "one problem",
            ReviewDocuments.Finding(
                severity: "Critical",
                category: "security",
                file: "src/One.cs",
                line: 42,
                title: "Secret in source",
                rationale: "The literal is a credential.",
                suggestedFix: "Read it from the environment.",
                confidence: "medium",
                outOfScope: false)));

        Assert.True(result.IsValid);

        var finding = Assert.Single(result.Document!.Findings!);

        Assert.Equal(FindingSeverity.Critical, ReviewSchema.Severity(finding.Severity));
        Assert.Equal(FindingCategory.Security, ReviewSchema.Category(finding.Category));
        Assert.Equal(FindingConfidence.Medium, ReviewSchema.Confidence(finding.Confidence));
        Assert.Equal(42, finding.Line);
        Assert.Equal("Read it from the environment.", finding.SuggestedFix);
        Assert.False(finding.OutOfScope);
    }

    [Fact]
    public void The_bounds_the_schema_states_are_the_bounds_the_domain_enforces()
    {
        // A title the schema allowed and the Finding constructor refused would be a review
        // that validated and then could not be written down.
        Assert.Equal(80, ReviewSchema.TitleMaxLength);

        var finding = new Finding(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            FindingSeverity.Info,
            FindingCategory.Convention,
            new string('t', ReviewSchema.TitleMaxLength),
            "r",
            "src/One.cs",
            1,
            null,
            FindingConfidence.Low,
            false,
            DateTimeOffset.UnixEpoch);

        Assert.Equal(ReviewSchema.TitleMaxLength, finding.Title.Length);
    }
}
