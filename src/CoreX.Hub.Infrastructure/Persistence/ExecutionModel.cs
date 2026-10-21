using System.Globalization;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Infrastructure.Persistence;

/// <summary>Mapping for the execution aggregate (6 of the 15 records).</summary>
internal static class ExecutionModel
{
    /// <summary>
    /// Filter for the "at most one active execution per task" unique index
    /// (architecture P.6). Derived from <see cref="ExecutionStateRules.NonTerminalStates"/>
    /// so the index and the domain classification can never drift apart silently;
    /// an architecture test asserts they agree.
    /// </summary>
    public static string ActiveExecutionIndexFilter { get; } =
        string.Create(
            CultureInfo.InvariantCulture,
            $"\"State\" IN ({string.Join(", ", ExecutionStateRules.NonTerminalStates.Select(s => $"'{s}'"))})");

    public static void Configure(ModelBuilder b, HubDbContext ctx)
    {
        b.Entity<Execution>(e =>
        {
            e.ToTable("Executions");
            e.HasKey(x => x.Id);
            e.Property(x => x.BaseCommitSha).HasMaxLength(40).IsRequired();
            e.Property(x => x.BranchName).HasMaxLength(200).IsRequired();
            e.Property(x => x.WorktreePath).HasMaxLength(400).IsRequired();
            e.Property(x => x.ContextPackHash).HasMaxLength(64);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.ResultCommitSha).HasMaxLength(40);
            e.Property(x => x.UnexpectedCommitShas).HasMaxLength(4000);
            e.Property(x => x.WorkspaceState).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Ignore(x => x.IsTerminal);
            e.Ignore(x => x.WorktreeMustBePreserved);

            e.HasIndex(x => new { x.TaskId, x.AttemptNo }).IsUnique().HasDatabaseName("UX_Executions_Task_Attempt");
            e.HasIndex(x => x.TaskId)
                .IsUnique()
                .HasFilter(ActiveExecutionIndexFilter)
                .HasDatabaseName("UX_Executions_ActivePerTask");
            e.HasIndex(x => new { x.OrganizationId, x.State }).HasDatabaseName("IX_Executions_Org_State");

            e.HasOne<HubTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });

        b.Entity<ExecutionStep>(e =>
        {
            e.ToTable("ExecutionSteps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.StateReason).HasMaxLength(1000);
            e.HasIndex(x => new { x.ExecutionId, x.Order }).IsUnique().HasDatabaseName("UX_ExecutionSteps_Exec_Order");
            e.HasOne<Execution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });

        b.Entity<CommandResult>(e =>
        {
            e.ToTable("CommandResults");
            e.HasKey(x => x.Id);
            e.Property(x => x.StepName).HasMaxLength(100).IsRequired();
            e.Property(x => x.Executable).HasMaxLength(400).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Required).IsRequired();
            e.Property<List<string>>("_args")
                .HasColumnName("Args")
                .HasConversion(HubValueConverters.StringList, HubValueConverters.StringListComparer)
                .IsRequired();
            e.Ignore(x => x.Args);
            e.Ignore(x => x.IsPass);
            e.HasIndex(x => new { x.ExecutionStepId, x.Order })
                .IsUnique()
                .HasDatabaseName("UX_CommandResults_Step_Order");

            // A Machine acceptance criterion names a step, so one step name under one
            // execution has to mean one row. Two would not be a tie to break later - they
            // would be machine truth that contradicts itself, and the database refuses it
            // at the point it would be created rather than at the point it is read.
            e.HasIndex(x => new { x.ExecutionStepId, x.StepName })
                .IsUnique()
                .HasDatabaseName("UX_CommandResults_Step_Name");
            e.HasOne<ExecutionStep>().WithMany().HasForeignKey(x => x.ExecutionStepId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });

        b.Entity<Finding>(e =>
        {
            e.ToTable("Findings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.Confidence).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Title).HasMaxLength(80).IsRequired();
            e.Property(x => x.Rationale).HasMaxLength(2000).IsRequired();
            e.Property(x => x.File).HasMaxLength(400);
            e.Property(x => x.SuggestedFix).HasMaxLength(2000);
            e.HasIndex(x => new { x.ExecutionStepId, x.Severity }).HasDatabaseName("IX_Findings_Step_Severity");
            e.HasOne<ExecutionStep>().WithMany().HasForeignKey(x => x.ExecutionStepId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });

        b.Entity<EvidenceArtifact>(e =>
        {
            e.ToTable("EvidenceArtifacts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.MediaType).HasMaxLength(100).IsRequired();
            e.HasIndex(x => new { x.ExecutionId, x.Kind }).HasDatabaseName("IX_EvidenceArtifacts_Exec_Kind");

            // Content addressed store: the same blob may be referenced many times.
            e.HasIndex(x => x.Sha256).HasDatabaseName("IX_EvidenceArtifacts_Sha256");
            e.HasOne<Execution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });

        b.Entity<AcceptanceDecision>(e =>
        {
            e.ToTable("AcceptanceDecisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Decision).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            e.Property(x => x.Reason).HasMaxLength(2000);
            e.Property(x => x.OverrideUsed).IsRequired();

            // ADR-0028: a person, or a repository's autopilot.
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property<List<HumanCriterionVerdictRecord>>("_humanCriterionVerdicts")
                .HasColumnName("HumanCriterionVerdicts")
                .HasConversion(HubValueConverters.HumanVerdicts, HubValueConverters.HumanVerdictsComparer)
                .IsRequired();
            e.Ignore(x => x.HumanCriterionVerdicts);

            // ADR-0014: exactly one immutable decision per execution.
            e.HasIndex(x => x.ExecutionId).IsUnique().HasDatabaseName("UX_AcceptanceDecisions_Execution");
            e.HasOne<Execution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });
    }
}
