using System.Reflection;
using CoreX.Hub.Domain.Common;
using CoreX.Hub.Domain.Executions;

namespace CoreX.Hub.Domain.Tests;

/// <summary>
/// What an execution is allowed to say about the commit it was based on.
///
/// The base commit is not a field that happens to hold a hash. It is the identity a
/// baseline is cached under, the identity a context pack is built from and the identity
/// every captured diff is measured against, so a value that could be invented now and
/// corrected later would make all three describe a commit nobody ever measured. These tests
/// are the aggregate refusing to let that happen.
/// </summary>
public sealed class ExecutionProvenanceTests
{
    private const string PinnedBase = "1f0c2b9a4d6e8f10325476981badc0ffee1234ab";
    private const string OtherBase = "abcdef0123456789abcdef0123456789abcdef01";
    private const string ResultCommit = "9876543210fedcba9876543210fedcba98765432";
    private const string NullObjectId = "0000000000000000000000000000000000000000";

    private static Execution NewExecution(string baseCommitSha = PinnedBase) =>
        new(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            1,
            baseCommitSha,
            "agent/HUB-001/1",
            "(not created yet)",
            null,
            DomainFixtures.Now);

    [Theory]
    [InlineData(NullObjectId)]
    [InlineData("pending")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("1f0c2b9a")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void A_placeholder_is_not_a_base_commit(string candidate)
    {
        // The null object id is the interesting one: it is the right length, it is
        // hexadecimal, and it is precisely what gets reached for when a row has to exist
        // before a commit has been resolved. Naming no object is not a shape problem.
        var rejected = Assert.Throws<DomainException>(() => NewExecution(candidate));

        Assert.Contains("baseCommitSha", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_placeholder_is_not_a_result_commit_either()
    {
        var execution = NewExecution();

        Assert.Throws<DomainException>(
            () => execution.RecordResultCommit(NullObjectId, DomainFixtures.Now));
    }

    [Fact]
    public void The_base_commit_cannot_be_moved_by_a_prepared_workspace()
    {
        var execution = NewExecution();

        var refused = Assert.Throws<DomainException>(() => execution.RecordWorkspace(
            OtherBase, "agent/HUB-001/1", @"D:\cx\hub-dev\hub-sample\HUB-001-1", DomainFixtures.Now));

        Assert.Contains(PinnedBase, refused.Message, StringComparison.Ordinal);
        Assert.Contains(OtherBase, refused.Message, StringComparison.Ordinal);

        // Fails closed: nothing at all was recorded, so a caller cannot end up with half a
        // workspace attached to the wrong provenance.
        Assert.Equal(PinnedBase, execution.BaseCommitSha);
        Assert.Equal("(not created yet)", execution.WorktreePath);
    }

    [Fact]
    public void A_workspace_at_the_same_commit_records_only_where_it_is()
    {
        var execution = NewExecution();

        execution.RecordWorkspace(
            PinnedBase.ToUpperInvariant(),
            "agent/HUB-001/1",
            @"D:\cx\hub-dev\hub-sample\HUB-001-1",
            DomainFixtures.Now);

        Assert.Equal(PinnedBase, execution.BaseCommitSha);
        Assert.Equal(@"D:\cx\hub-dev\hub-sample\HUB-001-1", execution.WorktreePath);
    }

    [Fact]
    public void Nothing_outside_the_constructor_can_write_the_base_commit()
    {
        var property = typeof(Execution).GetProperty(nameof(Execution.BaseCommitSha))!;

        Assert.Null(property.GetSetMethod(nonPublic: false));

        // And no method offers to set it under another name: the only parameter that names
        // a base commit belongs to the confirmation above, which compares rather than
        // assigns.
        var setters = typeof(Execution)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetParameters().Any(p =>
                p.Name is not null
                && p.Name.Contains("baseCommit", StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Name)
            .ToList();

        Assert.Equal(["RecordWorkspace"], setters);
    }

    [Fact]
    public void A_cancelled_execution_cannot_carry_a_result_commit()
    {
        var execution = NewExecution();
        execution.RecordResultCommit(ResultCommit, DomainFixtures.Now);

        var refused = Assert.Throws<DomainException>(
            () => execution.Terminate(ExecutionState.Cancelled, DomainFixtures.Now));

        Assert.Contains(ResultCommit, refused.Message, StringComparison.Ordinal);
        Assert.Equal(ResultCommit, execution.ResultCommitSha);
        Assert.False(execution.IsTerminal);
    }

    [Fact]
    public void An_interrupted_execution_may_carry_one_because_it_really_does()
    {
        // A host that died after the commit was created holds one. Refusing to say so would
        // hide a real commit, which is the opposite of what the invariant is for.
        var execution = NewExecution();
        execution.RecordResultCommit(ResultCommit, DomainFixtures.Now);

        execution.Terminate(ExecutionState.Interrupted, DomainFixtures.Now);

        Assert.Equal(ExecutionState.Interrupted, execution.State);
        Assert.Equal(ResultCommit, execution.ResultCommitSha);
    }
}
