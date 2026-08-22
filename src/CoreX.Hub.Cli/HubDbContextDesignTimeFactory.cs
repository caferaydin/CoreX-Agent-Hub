using CoreX.Hub.Application.Abstractions;
using CoreX.Hub.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Design;

namespace CoreX.Hub.Cli;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time. It never touches the real data
/// directory; migrations are generated against a throwaway path.
/// </summary>
public sealed class HubDbContextDesignTimeFactory : IDesignTimeDbContextFactory<HubDbContext>
{
    public HubDbContext CreateDbContext(string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "corex-hub-design.db");
        return new HubDbContext(HubDatabase.BuildOptions(path), new OrganizationScope());
    }
}
