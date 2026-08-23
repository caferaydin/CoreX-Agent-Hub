using CoreX.Hub.Application.Verification;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Verification;

namespace CoreX.Hub.ArchitectureTests;

/// <summary>
/// Guards for the machine truth a Machine acceptance criterion is answered from.
///
/// The value is written by one process and read by another, so the risks are drift and
/// shortcuts: two status vocabularies quietly diverging, or somebody deciding that a zero
/// exit code is close enough to a pass. Both would be invisible in a passing test suite and
/// permanent in the database.
/// </summary>
public sealed class MachineTruthInvariantTests
{
    [Fact]
    public void The_two_status_vocabularies_still_describe_the_same_thing()
    {
        // The domain owns its own copy because the domain depends on nothing. That copy is
        // only safe while it can express everything the verification module can report, so
        // a member added on either side has to show up here rather than in a persisted row
        // that means the wrong thing.
        var verification = Enum.GetNames<VerificationStepStatus>().Order(StringComparer.Ordinal).ToList();

        var domain = Enum.GetNames<VerificationCommandStatus>()
            .Where(n => !string.Equals(n, nameof(VerificationCommandStatus.Unavailable), StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(verification, domain);

        // Unavailable is the domain's own addition: a fail-closed reading for rows written
        // before this truth was durable, never something a run can report.
        Assert.DoesNotContain(nameof(VerificationCommandStatus.Unavailable), verification);
    }

    [Fact]
    public void The_conversion_between_them_is_exhaustive_and_fails_closed()
    {
        var gateway = File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "VerificationGateway.cs", StringComparison.Ordinal)));

        // Every member is named explicitly, so the compiler and the reader both see the
        // whole mapping.
        foreach (var status in Enum.GetNames<VerificationStepStatus>())
        {
            Assert.Contains($"VerificationStepStatus.{status}", gateway, StringComparison.Ordinal);
        }

        // And the fallback arm throws rather than choosing a status. A default that picked
        // Failed would look defensive and would still be a value nobody observed.
        Assert.Contains("_ => throw new ArgumentOutOfRangeException(", gateway, StringComparison.Ordinal);

        // The legacy reading is never produced by a conversion.
        Assert.DoesNotContain(
            $"VerificationCommandStatus.{nameof(VerificationCommandStatus.Unavailable)}",
            gateway,
            StringComparison.Ordinal);
    }

    [Fact]
    public void No_production_code_answers_machine_truth_from_an_exit_code()
    {
        // ParseFailed exits zero. Anything that treated a zero exit code as a pass would
        // report work as verified when the Hub could not read what verification produced.
        //
        // Exactly one place is allowed to look at an exit code at all: the engine that
        // watched the process and is deciding what its status IS. Everywhere downstream the
        // status already exists, and consulting the exit code again could only disagree
        // with it.
        string[] shortcuts = ["ExitCode == 0", "ExitCode is 0", "ExitCode.GetValueOrDefault() == 0"];

        // Everything in the machine-truth chain: whatever names the record or its status is
        // by definition reading or writing the value a criterion will be answered from. The
        // scan follows that chain rather than a fixed list, so a new participant is covered
        // the moment it joins.
        var chain = RepoLayout.ProductionSourceFiles
            .Where(f => !string.Equals(Path.GetFileName(f), "VerificationEngine.cs", StringComparison.Ordinal))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return Mentions(text, nameof(Domain.Executions.CommandResult))
                       || Mentions(text, nameof(VerificationCommandStatus));
            })
            .ToList();

        Assert.NotEmpty(chain);

        foreach (var file in chain)
        {
            var code = CodeOnly(File.ReadAllText(file));
            var name = RepoLayout.Relative(file);

            foreach (var shortcut in shortcuts)
            {
                Assert.False(code.Contains(shortcut, StringComparison.Ordinal), $"{name}: {shortcut}");
            }
        }

        // The exemption is not vacuous: the engine really is where a live process becomes a
        // status, and it maps to one rather than to a boolean.
        var engine = File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "VerificationEngine.cs", StringComparison.Ordinal)));

        Assert.Contains("ExitCode == 0", engine, StringComparison.Ordinal);
        Assert.Contains("VerificationStepStatus.Passed", engine, StringComparison.Ordinal);

        // The one rule that decides it is spelled out on the record itself.
        var commandResult = File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "CommandResult.cs", StringComparison.Ordinal)));

        Assert.Contains(
            "Status == VerificationCommandStatus.Passed",
            commandResult,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Machine_truth_is_read_through_the_execution_that_produced_it()
    {
        var store = File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "EfHubStore.cs", StringComparison.Ordinal)));

        // Execution to Verify step to commands. A query over CommandResults at large could
        // reach a baseline measured for a different purpose in a disposable tree.
        Assert.Contains("ExecutionStepKind.Verify", store, StringComparison.Ordinal);

        var reader = File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "VerificationTruthReader.cs", StringComparison.Ordinal)));

        // The resolver reports and never guesses: no first, no last, no ordering preference
        // standing in for an answer.
        foreach (var guess in new[] { ".First(", ".FirstOrDefault(", ".Last(", ".OrderByDescending(" })
        {
            Assert.False(reader.Contains(guess, StringComparison.Ordinal), guess);
        }

        Assert.Contains(nameof(MachineTruthProblem.Ambiguous), reader, StringComparison.Ordinal);
    }

    [Fact]
    public void Recorded_machine_truth_has_no_way_to_be_edited()
    {
        var commandResult = CodeOnly(File.ReadAllText(RepoLayout.ProductionSourceFiles.Single(f =>
            string.Equals(Path.GetFileName(f), "CommandResult.cs", StringComparison.Ordinal))));

        // Acceptance will read this. It must never be able to argue with it.
        foreach (var mutator in new[] { "SetStatus", "ForcePass", "OverrideResult", "RecordOutcome", "public void " })
        {
            Assert.False(commandResult.Contains(mutator, StringComparison.Ordinal), mutator);
        }
    }

    /// <summary>
    /// Whether the text names exactly this type, rather than one whose name merely ends
    /// with it. GitCommandResult and VerificationCommandResult are different records
    /// belonging to different modules, and neither is machine truth about a criterion.
    /// </summary>
    private static bool Mentions(string text, string typeName)
    {
        var from = 0;

        while (true)
        {
            var at = text.IndexOf(typeName, from, StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            if (at == 0 || !char.IsLetter(text[at - 1]))
            {
                return true;
            }

            from = at + typeName.Length;
        }
    }

    private static string CodeOnly(string source) =>
        string.Join(
            "\n",
            source.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
}
