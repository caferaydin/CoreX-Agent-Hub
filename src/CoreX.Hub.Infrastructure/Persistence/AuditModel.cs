using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Infrastructure.Persistence;

/// <summary>Mapping for the append-only records (2 of the 15).</summary>
internal static class AuditModel
{
    public static void Configure(ModelBuilder b, HubDbContext ctx)
    {
        b.Entity<AuditEvent>(e =>
        {
            e.ToTable("AuditEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Actor).HasMaxLength(200).IsRequired();
            e.Property(x => x.Summary).HasMaxLength(2000).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.HasIndex(x => x.OccurredAtUtc).HasDatabaseName("IX_AuditEvents_OccurredAt");
            e.HasIndex(x => x.TaskId).HasDatabaseName("IX_AuditEvents_Task");
            e.HasIndex(x => x.ExecutionId).HasDatabaseName("IX_AuditEvents_Execution");

            e.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId)
                .OnDelete(DeleteBehavior.Restrict);

            // System level rows carry no organization and stay visible under any scope.
            e.HasQueryFilter(x =>
                ctx.CurrentOrganizationId == null
                || x.OrganizationId == null
                || x.OrganizationId == ctx.CurrentOrganizationId);
        });

        b.Entity<OrchestrationEvent>(e =>
        {
            e.ToTable("OrchestrationEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Message).HasMaxLength(1000).IsRequired();
            e.HasIndex(x => new { x.ExecutionId, x.OccurredAtUtc })
                .HasDatabaseName("IX_OrchestrationEvents_Exec_OccurredAt");
            e.HasOne<Execution>().WithMany().HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(x => ctx.CurrentOrganizationId == null || x.OrganizationId == ctx.CurrentOrganizationId);
        });
    }
}
