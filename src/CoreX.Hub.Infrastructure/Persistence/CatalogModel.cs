using CoreX.Hub.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Infrastructure.Persistence;

/// <summary>Mapping for the catalog aggregate (5 of the 15 records).</summary>
internal static class CatalogModel
{
    public static void Configure(ModelBuilder b, HubDbContext ctx)
    {
        b.Entity<Organization>(e =>
        {
            e.ToTable("Organizations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Slug).HasConversion(HubValueConverters.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.FilesystemRoot).HasMaxLength(400).IsRequired();
            e.Property(x => x.IsolationLevel).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.RequiresHardIsolation).IsRequired();
            e.Property<List<string>>("_environmentAllowlist")
                .HasColumnName("EnvironmentAllowlist")
                .HasConversion(HubValueConverters.StringList, HubValueConverters.StringListComparer)
                .IsRequired();
            e.Ignore(x => x.EnvironmentAllowlist);
            e.Ignore(x => x.CanStartExecution);
            e.Ignore(x => x.BlockReason);
            e.HasIndex(x => x.Slug).IsUnique().HasDatabaseName("UX_Organizations_Slug");

            // The organization IS the scope root, so it filters on its own key.
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.Id == ctx.CurrentOrganizationId);
        });

        b.Entity<Project>(e =>
        {
            e.ToTable("Projects");
            e.HasKey(x => x.Id);
            e.Property(x => x.Slug).HasConversion(HubValueConverters.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.TaskKeyPrefix).HasMaxLength(10).IsRequired();
            e.HasIndex(x => new { x.OrganizationId, x.Slug }).IsUnique().HasDatabaseName("UX_Projects_Org_Slug");
            e.HasIndex(x => new { x.OrganizationId, x.TaskKeyPrefix })
                .IsUnique()
                .HasDatabaseName("UX_Projects_Org_TaskKeyPrefix");
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });

        b.Entity<Domain.Catalog.Repository>(e =>
        {
            e.ToTable("Repositories");
            e.HasKey(x => x.Id);
            e.Property(x => x.Slug).HasConversion(HubValueConverters.Slug).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.LocalPath).HasMaxLength(400).IsRequired();
            e.Property(x => x.DefaultBranch).HasMaxLength(200).IsRequired();
            e.Property<List<string>>("_protectedBranches")
                .HasColumnName("ProtectedBranches")
                .HasConversion(HubValueConverters.StringList, HubValueConverters.StringListComparer)
                .IsRequired();
            e.Ignore(x => x.ProtectedBranches);
            e.HasIndex(x => new { x.ProjectId, x.Slug }).IsUnique().HasDatabaseName("UX_Repositories_Project_Slug");

            // A managed working directory may back exactly one repository record.
            e.HasIndex(x => x.LocalPath).IsUnique().HasDatabaseName("UX_Repositories_LocalPath");
            e.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });

        b.Entity<VerificationProfile>(e =>
        {
            e.ToTable("VerificationProfiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.WorkingDirectory).HasMaxLength(400).IsRequired();
            e.Property(x => x.IsDefault).IsRequired();
            e.Property<List<VerificationStep>>("_steps")
                .HasColumnName("Steps")
                .HasConversion(HubValueConverters.VerificationSteps, HubValueConverters.VerificationStepsComparer)
                .IsRequired();
            e.Ignore(x => x.Steps);
            e.Ignore(x => x.StepNames);
            e.HasIndex(x => new { x.RepositoryId, x.Name })
                .IsUnique()
                .HasDatabaseName("UX_VerificationProfiles_Repo_Name");

            // At most one default profile per repository.
            e.HasIndex(x => x.RepositoryId)
                .IsUnique()
                .HasFilter("\"IsDefault\" = 1")
                .HasDatabaseName("UX_VerificationProfiles_Repo_Default");
            e.HasOne<Domain.Catalog.Repository>().WithMany().HasForeignKey(x => x.RepositoryId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });

        b.Entity<AgentProfile>(e =>
        {
            e.ToTable("AgentProfiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(100).IsRequired();
            e.Property(x => x.RuntimeKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.PermissionProfile).HasConversion<string>().HasMaxLength(30).IsRequired();
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.Effort).HasMaxLength(20);
            e.HasIndex(x => new { x.OrganizationId, x.Name })
                .IsUnique()
                .HasDatabaseName("UX_AgentProfiles_Org_Name");
            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });
    }
}
