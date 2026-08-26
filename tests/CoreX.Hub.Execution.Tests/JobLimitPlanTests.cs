using CoreX.Hub.Execution.Interop;
using CoreX.Hub.Execution.Processes;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// What a set of requested ceilings becomes in Win32 terms.
///
/// This is the half of quota enforcement that fails silently. A flag set without its value,
/// a value set without its flag, or a number written into the neighbouring field - Windows
/// accepts all three and simply does not limit what anyone thought it would.
/// <c>JobMemoryLimit</c> and <c>ProcessMemoryLimit</c> are adjacent in the same structure and
/// differ only in whether they apply to the job or to each process in it, so the mapping is
/// asserted field by field rather than trusted.
/// </summary>
public sealed class JobLimitPlanTests
{
    [Fact]
    public void With_no_limits_the_job_still_reaps_its_tree_and_nothing_else()
    {
        var plan = JobLimitPlan.For(ProcessResourceLimits.None);

        // The one flag that is never optional: it is what makes the job a containment
        // boundary at all, and it must survive every combination below.
        Assert.Equal(JobLimitPlan.KillOnJobClose, plan.LimitFlags);

        // No quota was asked for, so no quota field is meaningful. They stay zero, which is
        // where Windows ignores them because the flags that would read them are not set.
        Assert.Equal(0u, plan.ActiveProcessLimit);
        Assert.Equal(0ul, plan.JobMemoryLimitBytes);
        Assert.Equal(0ul, plan.ProcessMemoryLimitBytes);
    }

    [Fact]
    public void A_process_count_ceiling_sets_its_own_flag_and_its_own_field()
    {
        var plan = JobLimitPlan.For(new ProcessResourceLimits { ActiveProcessLimit = 3 });

        Assert.Equal(JobLimitPlan.KillOnJobClose | JobLimitPlan.ActiveProcess, plan.LimitFlags);
        Assert.Equal(3u, plan.ActiveProcessLimit);

        // Asking for one ceiling must not quietly enable another.
        Assert.Equal(0ul, plan.JobMemoryLimitBytes);
        Assert.Equal(0ul, plan.ProcessMemoryLimitBytes);
    }

    [Fact]
    public void A_job_memory_ceiling_lands_in_the_job_field_and_not_the_process_field()
    {
        var plan = JobLimitPlan.For(new ProcessResourceLimits { JobMemoryLimitBytes = 192 * 1024 * 1024 });

        Assert.Equal(JobLimitPlan.KillOnJobClose | JobLimitPlan.JobMemory, plan.LimitFlags);

        // The distinction the whole aggregate limit rests on: a per process ceiling would be
        // satisfied by twenty processes each staying just under it.
        Assert.Equal(192ul * 1024 * 1024, plan.JobMemoryLimitBytes);
        Assert.Equal(0ul, plan.ProcessMemoryLimitBytes);
        Assert.Equal(0u, plan.ActiveProcessLimit);
    }

    [Fact]
    public void A_process_memory_ceiling_lands_in_the_process_field_and_not_the_job_field()
    {
        var plan = JobLimitPlan.For(new ProcessResourceLimits { ProcessMemoryLimitBytes = 64 * 1024 * 1024 });

        Assert.Equal(JobLimitPlan.KillOnJobClose | JobLimitPlan.ProcessMemory, plan.LimitFlags);
        Assert.Equal(64ul * 1024 * 1024, plan.ProcessMemoryLimitBytes);
        Assert.Equal(0ul, plan.JobMemoryLimitBytes);
    }

    [Fact]
    public void Every_ceiling_together_composes_every_flag_and_keeps_the_values_apart()
    {
        var plan = JobLimitPlan.For(new ProcessResourceLimits
        {
            ActiveProcessLimit = 4,
            JobMemoryLimitBytes = 300,
            ProcessMemoryLimitBytes = 100,
        });

        Assert.Equal(
            JobLimitPlan.KillOnJobClose
            | JobLimitPlan.ActiveProcess
            | JobLimitPlan.JobMemory
            | JobLimitPlan.ProcessMemory,
            plan.LimitFlags);

        Assert.Equal(4u, plan.ActiveProcessLimit);
        Assert.Equal(300ul, plan.JobMemoryLimitBytes);
        Assert.Equal(100ul, plan.ProcessMemoryLimitBytes);
    }

    [Theory]
    [InlineData(JobLimitPlan.ActiveProcess)]
    [InlineData(JobLimitPlan.JobMemory)]
    [InlineData(JobLimitPlan.ProcessMemory)]
    public void No_combination_ever_drops_kill_on_close(uint _)
    {
        // Stated once per optional flag, because losing kill-on-close would be the worst
        // possible way for a quota feature to fail: the tree would outlive the run.
        foreach (var limits in new[]
                 {
                     ProcessResourceLimits.None,
                     new ProcessResourceLimits { ActiveProcessLimit = 1 },
                     new ProcessResourceLimits { JobMemoryLimitBytes = 1 },
                     new ProcessResourceLimits { ProcessMemoryLimitBytes = 1 },
                     new ProcessResourceLimits
                     {
                         ActiveProcessLimit = 1,
                         JobMemoryLimitBytes = 1,
                         ProcessMemoryLimitBytes = 1,
                     },
                 })
        {
            Assert.Equal(
                JobLimitPlan.KillOnJobClose,
                JobLimitPlan.For(limits).LimitFlags & JobLimitPlan.KillOnJobClose);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_process_count_that_is_not_a_limit_is_refused(int value)
    {
        var limits = new ProcessResourceLimits { ActiveProcessLimit = value };

        // Not clamped to something sensible. A caller asking for a ceiling of zero has said
        // something the system cannot act on, and guessing what they meant would answer a
        // security question on their behalf.
        var refusal = Assert.Throws<ProcessRequestException>(limits.Validate);

        Assert.Contains(nameof(ProcessResourceLimits.ActiveProcessLimit), refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4096)]
    public void A_memory_ceiling_that_is_not_a_limit_is_refused(long value)
    {
        Assert.Throws<ProcessRequestException>(
            new ProcessResourceLimits { JobMemoryLimitBytes = value }.Validate);

        Assert.Throws<ProcessRequestException>(
            new ProcessResourceLimits { ProcessMemoryLimitBytes = value }.Validate);
    }

    [Fact]
    public void Absent_is_not_zero()
    {
        // The managed contract cannot express "limit of nothing", so there is no sentinel to
        // misread: an unset ceiling is a ceiling that was never requested.
        ProcessResourceLimits.None.Validate();

        Assert.False(ProcessResourceLimits.None.Any);
        Assert.Null(ProcessResourceLimits.None.ActiveProcessLimit);
        Assert.True(new ProcessResourceLimits { ActiveProcessLimit = 1 }.Any);
    }
}
