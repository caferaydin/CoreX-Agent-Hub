using System.Runtime.Versioning;
using System.Text;
using CoreX.Hub.Agents.Abstractions;
using CoreX.Hub.Agents.Codex;
using CoreX.Hub.Application.Review;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Composition.Orchestration;

namespace CoreX.Hub.Integration.Tests;

/// <summary>
/// The seam between a real provider adapter and the provider-neutral engine that judges it.
///
/// Both halves are already covered on their own: the adapter turns each broken stream into a
/// failed run, and the engine turns a failed run into ReviewUnusable. What is only true when
/// they are wired together is that no shape falls between them - and that is exactly where a
/// second provider is most likely to need "just one" special case.
///
/// So the real CodexRuntime runs here, over a scripted process, into the real ReviewEngine.
/// Nothing about the engine knows which provider it got, and every one of these shapes must
/// arrive at the same answer the first provider's do.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CodexReviewUnusableTests
{
    public static TheoryData<string, string, string> BrokenStreams() => new()
    {
        {
            "the provider said the turn failed",
            CodexJson.Thread + CodexJson.Error("Your access token could not be refreshed")
                + CodexJson.TurnFailed("Your access token could not be refreshed"),
            "failed turn"
        },
        {
            "authentication was refused",
            CodexJson.Thread + CodexJson.Error("401 Unauthorized: please run codex login")
                + CodexJson.TurnFailed("401 Unauthorized"),
            "failed turn"
        },
        {
            "the stream stopped before any terminal event",
            CodexJson.Thread + """{"type":"turn.started"}""" + "\n",
            "no terminal event"
        },
        {
            "the provider never named the thread it ran",
            """{"type":"turn.started"}""" + "\n" + CodexJson.Message(CodexJson.CleanFindings)
                + CodexJson.TurnCompleted,
            "never named the thread"
        },
        {
            "the turn finished and produced no document at all",
            CodexJson.Thread + CodexJson.TurnCompleted,
            string.Empty
        },
        {
            "the turn finished and produced prose instead of a document",
            CodexJson.Thread + CodexJson.Message("Looks fine to me, no findings.") + CodexJson.TurnCompleted,
            string.Empty
        },
    };

    [Theory]
    [MemberData(nameof(BrokenStreams))]
    public async Task A_review_that_cannot_be_trusted_is_unusable_however_it_broke(
        string because,
        string stream,
        string expectedDetail)
    {
        using var lab = ReviewLab.Create(new ScriptedProcesses(stream, exitCode: 0));

        var result = await lab.Engine.ReviewAsync(lab.Request(), CancellationToken.None);

        // The one answer that matters: no usable review exists, whatever the provider said
        // about itself. Never Clean - a review nobody can read is not a review that found
        // nothing.
        Assert.Equal(ReviewVerdict.Unusable, result.Verdict);

        // Named in the failure message, so a red test says which shape regressed.
        Assert.True(result.Verdict == ReviewVerdict.Unusable, because);

        // And the step is not reported as a success that found nothing.
        Assert.NotEqual(ReviewStepFate.Succeeded, result.StepFate);

        if (expectedDetail.Length > 0)
        {
            Assert.Contains(expectedDetail, result.Detail, StringComparison.Ordinal);
        }

        // The evidence survives the failure. A run whose output was discarded because it went
        // wrong is a run nobody can diagnose afterwards.
        Assert.NotEmpty(result.Attempts);
        Assert.All(result.Attempts, a => Assert.False(string.IsNullOrEmpty(a.Stdout) && string.IsNullOrEmpty(a.Stderr)));

        // And the snapshot went away regardless.
        Assert.Null(result.SnapshotResidual);
    }

    [Theory]
    [InlineData(AgentProcessTermination.TimedOut, "ran out of time", ReviewStepFate.TimedOut)]
    [InlineData(AgentProcessTermination.Cancelled, "was cancelled", ReviewStepFate.Cancelled)]
    [InlineData(AgentProcessTermination.StartFailed, "did not complete", ReviewStepFate.Failed)]
    public async Task A_reviewer_process_that_never_finished_is_unusable_too(
        AgentProcessTermination termination,
        string expectedDetail,
        ReviewStepFate expectedFate)
    {
        using var lab = ReviewLab.Create(
            new ScriptedProcesses(CodexJson.Thread + CodexJson.Message(CodexJson.CleanFindings), termination));

        var result = await lab.Engine.ReviewAsync(lab.Request(), CancellationToken.None);

        // Process truth wins over anything the stream said. The scripted stream above looks
        // like a perfectly good review right up to the moment the process is asked how it
        // ended.
        Assert.Equal(ReviewVerdict.Unusable, result.Verdict);

        // The engine says what happened in its own words rather than repeating a provider's.
        // That is the point: nothing downstream should have to learn one vendor's vocabulary
        // to find out that no review exists.
        Assert.Contains(expectedDetail, result.Detail, StringComparison.Ordinal);
        Assert.Equal(expectedFate, result.StepFate);
    }

    [Fact]
    public async Task The_engine_reached_all_of_this_without_a_single_line_about_this_provider()
    {
        // Non-vacuous companion to the guards: the type that decided every verdict above is
        // the same one Phase 7 shipped, and its source names no provider.
        var engine = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "CoreX.Hub.Application", "Review", "ReviewEngine.cs"));

        foreach (var word in new[] { "Codex", "Claude", "codex", "claude" })
        {
            Assert.DoesNotContain(word, engine, StringComparison.Ordinal);
        }

        // And it really did run: a scripted good review still comes out Clean, so the family
        // above is measuring failure rather than measuring a broken harness.
        using var lab = ReviewLab.Create(
            new ScriptedProcesses(
                CodexJson.Thread + CodexJson.Message(CodexJson.CleanFindings) + CodexJson.TurnCompleted,
                exitCode: 0));

        var result = await lab.Engine.ReviewAsync(lab.Request(), CancellationToken.None);

        Assert.Equal(ReviewVerdict.Clean, result.Verdict);
        Assert.Empty(result.Findings);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CoreX.Hub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("no repository root");
    }

    /// <summary>The provider's stream vocabulary, written the way the provider writes it.</summary>
    private static class CodexJson
    {
        public const string Thread = "{\"type\":\"thread.started\",\"thread_id\":\"01a03960-11ec-7991-a222-4d816a219218\"}\n";
        public const string TurnCompleted = "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":10,\"cached_input_tokens\":0,\"output_tokens\":2}}\n";

        public const string CleanFindings = "{\\\"findings\\\":[],\\\"summary\\\":\\\"No findings.\\\"}";

        public static string Message(string text) =>
            "{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\","
            + "\"type\":\"agent_message\",\"text\":\"" + text + "\"}}\n";

        public static string Error(string message) =>
            "{\"type\":\"error\",\"message\":\"" + message + "\"}\n";

        public static string TurnFailed(string message) =>
            "{\"type\":\"turn.failed\",\"error\":{\"message\":\"" + message + "\"}}\n";
    }

    /// <summary>A process that plays one scripted stream and ends the way it was told to.</summary>
    private sealed class ScriptedProcesses : IAgentProcessRunner
    {
        private readonly string _stdout;
        private readonly AgentProcessTermination _termination;
        private readonly int? _exitCode;

        public ScriptedProcesses(string stdout, int exitCode)
        {
            _stdout = stdout;
            _termination = AgentProcessTermination.Exited;
            _exitCode = exitCode;
        }

        public ScriptedProcesses(string stdout, AgentProcessTermination termination)
        {
            _stdout = stdout;
            _termination = termination;
            _exitCode = termination == AgentProcessTermination.Exited ? 0 : null;
        }

        public Task<AgentProcessResult> RunAsync(AgentProcessRequest request, CancellationToken cancellationToken)
        {
            // Version probes answer honestly so health is never what fails here.
            if (request.Arguments.Contains("--version"))
            {
                return Task.FromResult(Result("codex-cli 0.130.0\n", AgentProcessTermination.Exited, 0));
            }

            request.StandardOutputObserver?.Invoke(_stdout);

            return Task.FromResult(Result(_stdout, _termination, _exitCode));
        }

        private static AgentProcessResult Result(string stdout, AgentProcessTermination termination, int? exitCode) =>
            new()
            {
                Termination = termination,
                ExitCode = exitCode,
                Stdout = stdout,
                Stderr = "scripted reviewer stderr",
                StartedAtUtc = DateTimeOffset.UnixEpoch,
                EndedAtUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
                StartFailureMessage = termination == AgentProcessTermination.StartFailed
                    ? "the scripted executable could not be started"
                    : null,
            };
    }

    /// <summary>The real engine over the real adapter, with only the process boundary scripted.</summary>
    private sealed class ReviewLab : IDisposable
    {
        private ReviewLab(string root, ReviewEngine engine)
        {
            Root = root;
            Engine = engine;
        }

        public string Root { get; }

        public ReviewEngine Engine { get; }

        public static ReviewLab Create(IAgentProcessRunner processes)
        {
            var root = Path.Combine(Path.GetTempPath(), "corex-codex-unusable", Guid.CreateVersion7().ToString("N"));
            var snapshots = Path.Combine(root, "snapshots");
            var executable = Path.Combine(root, "codex.exe");
            var contract = Path.Combine(root, "reviewer.md");

            Directory.CreateDirectory(snapshots);
            File.WriteAllText(executable, "a path that exists", new UTF8Encoding(false));
            File.WriteAllText(contract, "# Reviewer contract", new UTF8Encoding(false));

            var runtime = new CodexRuntime(
                new CodexOptions
                {
                    ExecutablePath = executable,
                    AcceptedVersion = "0.130.0",
                    HealthProbeDirectory = root,
                },
                processes);

            return new ReviewLab(
                root,
                new ReviewEngine(
                    new SnapshotWorkspace(snapshots),
                    new RuntimeGateway(runtime),
                    new DiffReader(),
                    new ReviewEngineOptions { ReviewerPromptFile = contract }));
        }

        public ExecutionReviewRequest Request() => new()
        {
            ExecutionId = Guid.CreateVersion7(),
            RepositoryId = Guid.CreateVersion7(),
            ResultCommitSha = "6c73034b93dd559ae599e88747b024f7788830ae",
            ChangedPaths = ["docs/phase8-codex-reviewer-acceptance.md"],
            CommitDiff = new EvidenceArtifact(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                null,
                EvidenceKind.GitCommitDiff,
                new string('a', 64),
                12,
                "text/plain",
                0,
                DateTimeOffset.UnixEpoch),
        };

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        private sealed class SnapshotWorkspace(string root) : IReviewWorkspace
        {
            public Task<ReviewSnapshot?> PrepareAsync(Guid executionId, Guid repositoryId, string commitSha, string snapshotId, CancellationToken ct)
            {
                var path = Path.Combine(root, snapshotId);
                Directory.CreateDirectory(path);

                return Task.FromResult<ReviewSnapshot?>(new ReviewSnapshot(path, commitSha));
            }

            public Task<ReviewSnapshotIntegrity?> InspectAsync(ReviewSnapshot snapshot, CancellationToken ct) =>
                Task.FromResult<ReviewSnapshotIntegrity?>(
                    new ReviewSnapshotIntegrity(snapshot.CommitSha, "tree", true, "clean"));

            public Task<string?> ReleaseAsync(ReviewSnapshot snapshot, CancellationToken ct)
            {
                Directory.Delete(snapshot.SnapshotPath, recursive: true);

                return Task.FromResult<string?>(null);
            }
        }

        private sealed class DiffReader : IReviewEvidenceReader
        {
            public Task<string?> ReadAsync(EvidenceArtifact artifact, CancellationToken ct) =>
                Task.FromResult<string?>("diff --git a/docs/x.md b/docs/x.md\n+one line\n");
        }

        /// <summary>The same shape composition builds, minus the catalogue lookup.</summary>
        private sealed class RuntimeGateway(IAgentRuntime runtime) : IReviewRuntimeGateway
        {
            public async Task<ReviewRuntimeResult> RunAsync(ReviewRuntimeRequest request, CancellationToken ct)
            {
                var result = await runtime.RunAsync(
                    new AgentRunRequest
                    {
                        WorkingDirectory = request.WorkingDirectory,
                        SystemPromptFile = request.SystemPromptFile,
                        UserPrompt = request.UserPrompt,
                        PermissionProfile = Domain.Catalog.AgentPermissionProfile.ReadOnly,
                        ModelHint = "gpt-5.5",
                        EffortHint = "high",
                        StructuredOutputSchema = request.StructuredOutputSchema,
                        EnvironmentAllowlist = CodexInvocationBuilder.PermittedEnvironmentNames,
                        SoftTimeout = TimeSpan.FromSeconds(900),
                        IdleTimeout = TimeSpan.FromSeconds(1200),
                        HardTimeout = TimeSpan.FromSeconds(1800),
                        CorrelationId = request.SessionId,
                        SessionId = request.SessionId,
                    },
                    new Progress<AgentEvent>(_ => { }),
                    ct);

                return new ReviewRuntimeResult
                {
                    Outcome = result.Outcome.ToString(),
                    StructuredOutput = result.StructuredOutput,
                    Stdout = result.Stdout,
                    Stderr = result.Stderr,
                    ProviderSessionId = result.ProviderSessionId,
                    Diagnostic = result.Diagnostic,
                };
            }
        }
    }
}
