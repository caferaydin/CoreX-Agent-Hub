using System.Runtime.Versioning;
using CoreX.Hub.Cli.Context;
using CoreX.Hub.Context;
using CoreX.Hub.Domain.Tasks;

namespace CoreX.Hub.Context.Tests;

/// <summary>A disposable Hub owned context root plus a wired builder.</summary>
[SupportedOSPlatform("windows")]
public sealed class ContextFixture : IDisposable
{
    private ContextFixture(string root)
    {
        Root = root;
        ContextRoot = Path.Combine(root, "context");
        WorkspaceRoot = Path.Combine(root, "workspace");

        Directory.CreateDirectory(ContextRoot);
        Directory.CreateDirectory(WorkspaceRoot);

        Builder = new ContextPackBuilder(new FileSystemGuardContextPathGuard());
    }

    public string Root { get; }

    public string ContextRoot { get; }

    public string WorkspaceRoot { get; }

    public ContextPackBuilder Builder { get; }

    public static readonly Guid ExecutionId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static readonly Guid RepositoryId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public const string BaseCommit = "1234567890abcdef1234567890abcdef12345678";

    public static ContextFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "corex-ctx-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return new ContextFixture(root);
    }

    public ContextBuildRequest Request() => new()
    {
        ExecutionId = ExecutionId,
        ContextRoot = ContextRoot,
        TaskKey = "HUB-001",
        Objective = "Add a deterministic feature file to the sample repository.",
        AllowedScope = ["src/"],
        ForbiddenScope = ["infra/"],
        AttemptNo = 1,
        AcceptanceCriteria =
        [
            new ContextAcceptanceCriterion
            {
                Order = 1,
                Text = "The test suite passes.",
                Kind = VerificationKind.Machine,
                VerificationRef = "test",
            },
            new ContextAcceptanceCriterion
            {
                Order = 2,
                Text = "A reviewer agrees the change reads well.",
                Kind = VerificationKind.Human,
                HumanCheckHint = "look at the diff",
            },
        ],
        OrganizationRules = new ContextRuleDocument { Title = "hub-dev", Markdown = "Prefer small changes." },
        ProjectRules = new ContextRuleDocument { Title = "hub-sample", Markdown = "Keep the sample tiny." },
        RepositoryRules = new ContextRuleDocument { Title = "sample-repo", Markdown = "Do not touch infra." },
        Decisions =
        [
            new ContextDecisionDocument
            {
                Slug = "0002-logging",
                Title = "Logging stays structured",
                Markdown = "We log structured events, never free text.",
            },
            new ContextDecisionDocument
            {
                Slug = "0001-storage",
                Title = "SQLite is the store",
                Markdown = "One local database, WAL mode.",
            },
        ],
        RepositoryState = new ContextRepositoryState
        {
            RepositoryId = RepositoryId,
            BaseCommitSha = BaseCommit,
            DefaultBranch = "main",
            ProtectedBranches = ["main", "release"],
        },
    };

    public string Read(ContextPack pack, string relativePath) =>
        File.ReadAllText(Path.Combine(pack.PackPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
