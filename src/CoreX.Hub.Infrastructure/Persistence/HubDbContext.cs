using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Domain.Audit;
using CoreX.Hub.Domain.Catalog;
using CoreX.Hub.Domain.Executions;
using CoreX.Hub.Domain.Tasks;
using Microsoft.EntityFrameworkCore;

namespace CoreX.Hub.Infrastructure.Persistence;

/// <summary>
/// The 15 relational records of the accepted domain model (architecture D).
///
/// Every organization-owned set carries a global query filter driven by
/// <see cref="OrganizationScope"/>. ADR-0012: this is logical isolation, not an OS
/// security boundary.
/// </summary>
public sealed class HubDbContext(DbContextOptions<HubDbContext> options, OrganizationScope scope)
    : DbContext(options)
{
    private readonly OrganizationScope _scope = scope ?? throw new ArgumentNullException(nameof(scope));

    // Catalog aggregate
    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<Domain.Catalog.Repository> Repositories => Set<Domain.Catalog.Repository>();

    public DbSet<VerificationProfile> VerificationProfiles => Set<VerificationProfile>();

    public DbSet<AgentProfile> AgentProfiles => Set<AgentProfile>();

    // Task aggregate
    public DbSet<HubTask> Tasks => Set<HubTask>();

    public DbSet<AcceptanceCriterion> AcceptanceCriteria => Set<AcceptanceCriterion>();

    // Execution aggregate
    public DbSet<Execution> Executions => Set<Execution>();

    public DbSet<ExecutionStep> ExecutionSteps => Set<ExecutionStep>();

    public DbSet<CommandResult> CommandResults => Set<CommandResult>();

    public DbSet<Finding> Findings => Set<Finding>();

    public DbSet<EvidenceArtifact> EvidenceArtifacts => Set<EvidenceArtifact>();

    public DbSet<AcceptanceDecision> AcceptanceDecisions => Set<AcceptanceDecision>();

    // Audit
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<OrchestrationEvent> OrchestrationEvents => Set<OrchestrationEvent>();

    /// <summary>Exposed for query filters; EF parameterises this per query.</summary>
    internal Guid? CurrentOrganizationId => _scope.OrganizationId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        CatalogModel.Configure(modelBuilder, this);
        TaskModel.Configure(modelBuilder, this);
        ExecutionModel.Configure(modelBuilder, this);
        AuditModel.Configure(modelBuilder, this);

        base.OnModelCreating(modelBuilder);
    }
}
