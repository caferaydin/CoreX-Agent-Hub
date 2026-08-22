using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Infrastructure.Persistence;

/// <summary>Mapping for the task aggregate (2 of the 15 records).</summary>
internal static class TaskModel
{
    /// <summary>Field-backed navigation name used by the store when including criteria.</summary>
    public const string AcceptanceCriteriaNavigation = "_acceptanceCriteria";

    public static void Configure(ModelBuilder b, HubDbContext ctx)
    {
        b.Entity<HubTask>(e =>
        {
            e.ToTable("Tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.TaskKey).HasConversion(HubValueConverters.TaskKey).HasMaxLength(20).IsRequired();
            e.Property(x => x.Objective).HasMaxLength(4000).IsRequired();
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.BlockReason).HasMaxLength(500);

            // Architecture P.6: optimistic concurrency on the task aggregate.
            e.Property(x => x.Version).IsConcurrencyToken().IsRequired();

            e.Property<List<string>>("_allowedScope")
                .HasColumnName("AllowedScope")
                .HasConversion(HubValueConverters.StringList, HubValueConverters.StringListComparer)
                .IsRequired();
            e.Property<List<string>>("_forbiddenScope")
                .HasColumnName("ForbiddenScope")
                .HasConversion(HubValueConverters.StringList, HubValueConverters.StringListComparer)
                .IsRequired();
            e.Ignore(x => x.AllowedScope);
            e.Ignore(x => x.ForbiddenScope);
            e.Ignore(x => x.AcceptanceCriteria);
            e.Ignore(x => x.NextCriterionOrder);

            // Task keys are unique per project. Two organizations may legitimately share a
            // project prefix (for example ERP), so global uniqueness would be wrong.
            e.HasIndex(x => new { x.ProjectId, x.TaskKey }).IsUnique().HasDatabaseName("UX_Tasks_Project_TaskKey");
            e.HasIndex(x => new { x.OrganizationId, x.State }).HasDatabaseName("IX_Tasks_Org_State");

            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Domain.Catalog.Repository>().WithMany().HasForeignKey(x => x.RepositoryId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasMany<AcceptanceCriterion>(AcceptanceCriteriaNavigation)
                .WithOne()
                .HasForeignKey(c => c.TaskId)
                .OnDelete(DeleteBehavior.Cascade);

            e.Navigation(AcceptanceCriteriaNavigation).UsePropertyAccessMode(PropertyAccessMode.Field);

            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });

        b.Entity<AcceptanceCriterion>(e =>
        {
            e.ToTable("AcceptanceCriteria");
            e.HasKey(x => x.Id);
            e.Property(x => x.Text).HasMaxLength(500).IsRequired();

            // ADR-0015: nullable on purpose. A draft criterion may not have declared its
            // verification method yet; the ready gate is what rejects it.
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.VerificationRef).HasMaxLength(100);
            e.Property(x => x.HumanCheckHint).HasMaxLength(500);
            e.HasIndex(x => new { x.TaskId, x.Order }).IsUnique().HasDatabaseName("UX_AcceptanceCriteria_Task_Order");
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });
    }
}
