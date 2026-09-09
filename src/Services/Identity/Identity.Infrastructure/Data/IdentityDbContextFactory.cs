using Identity.Domain.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Identity.Infrastructure.Data;

/// <summary>Design-time factory used by `dotnet ef` when generating migrations.</summary>
public class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Host=localhost;Database=identitydb;Username=postgres;Password=postgres";

        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(IdentityDbContextFactory).Assembly.FullName))
            .UseOpenIddict()
            .Options;

        return new IdentityDbContext(options, new NullUserIdAccessor());
    }

    private sealed class NullUserIdAccessor : IUserIdAccessor
    {
        public string? GetCurrentUserId() => null;
    }
}
