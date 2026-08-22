using System.Xml;
using System.Xml.Linq;

namespace CoreX.Hub.Verification.Reports;

/// <summary>
/// Test reports are repository produced input, so they are parsed with everything
/// dangerous switched off: no DTD, no entity expansion, no resolver and therefore no
/// external entity or network access (Phase 4 brief section 18).
/// </summary>
public static class SafeXml
{
    /// <summary>
    /// Practical ceiling for a single report. Larger reports are refused deterministically
    /// rather than being loaded into memory.
    /// </summary>
    public const long MaxReportBytes = 16 * 1024 * 1024;

    public static XmlReaderSettings CreateSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        CloseInput = true,
    };

    /// <summary>
    /// Parses text into a document, mapping every rejection onto a deterministic failure
    /// rather than letting an exception escape into the engine.
    /// </summary>
    public static ReportParseResult TryLoad(string xml, out XDocument? document)
    {
        document = null;

        if (string.IsNullOrWhiteSpace(xml))
        {
            return ReportParseResult.Failed(ReportParseFailure.MalformedXml, "the report was empty");
        }

        try
        {
            using var stringReader = new StringReader(xml);
            using var reader = XmlReader.Create(stringReader, CreateSettings());
            document = XDocument.Load(reader, LoadOptions.None);
            return ReportParseResult.Ok(EmptySummary);
        }
        catch (XmlException ex) when (IsProhibitedConstruct(ex))
        {
            return ReportParseResult.Failed(
                ReportParseFailure.ProhibitedXmlConstruct,
                "the report declares a DTD, which is refused because it enables external entity resolution");
        }
        catch (XmlException ex)
        {
            return ReportParseResult.Failed(ReportParseFailure.MalformedXml, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ReportParseResult.Failed(ReportParseFailure.MalformedXml, ex.Message);
        }
    }

    /// <summary>
    /// XmlException does not expose a machine readable code for a prohibited DTD, so the
    /// message is matched. Both wordings map onto the same deterministic failure, and an
    /// unmatched wording still fails - as malformed, never as success.
    /// </summary>
    private static bool IsProhibitedConstruct(XmlException exception) =>
        exception.Message.Contains("DTD", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("DOCTYPE", StringComparison.OrdinalIgnoreCase);

    private static readonly TestSummary EmptySummary = new()
    {
        Format = TestReportFormat.Trx,
        Total = 0,
        Passed = 0,
        Failed = 0,
        Errors = 0,
        Skipped = 0,
    };

    /// <summary>Attribute lookup that ignores namespaces, because report dialects vary.</summary>
    public static string? AttributeValue(this System.Xml.Linq.XElement element, string name) =>
        element.Attributes().FirstOrDefault(a =>
            string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    public static int ReadInt(this System.Xml.Linq.XElement element, string name)
    {
        var raw = element.AttributeValue(name);
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : 0;
    }

    public static bool HasAttribute(this System.Xml.Linq.XElement element, string name) =>
        element.AttributeValue(name) is not null;

    public static IEnumerable<System.Xml.Linq.XElement> ByLocalName(
        this System.Xml.Linq.XContainer container,
        string localName) =>
        container.Descendants().Where(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
}
