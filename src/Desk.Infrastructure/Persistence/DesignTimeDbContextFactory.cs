using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Desk.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c> only; the connection string never leaves the environment.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DeskDbContext>
{
    public DeskDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Database=deskflow_design";
        var options = new DbContextOptionsBuilder<DeskDbContext>().UseNpgsql(connection).Options;
        return new DeskDbContext(options, new TenantContext());
    }
}
