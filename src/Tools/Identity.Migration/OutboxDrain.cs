using Identity.Domain.Interfaces;
using Identity.Infrastructure.Data;
using Identity.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Identity.Migration;

/// <summary>
/// Rollback guard: synchronously replays every pending outbox row into the monolith and exits 0 only when
/// the outbox is empty, so the monolith DB is provably current before the gateway is re-pointed to it.
/// </summary>
public class OutboxDrain(IConfiguration configuration)
{
    public async Task<int> RunAsync()
    {
        var identity = configuration.GetConnectionString("Identity") ?? throw new InvalidOperationException("ConnectionStrings:Identity missing");
        var monolith = configuration.GetConnectionString("Monolith") ?? throw new InvalidOperationException("ConnectionStrings:Monolith missing");

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        services.AddSingleton<IUserIdAccessor, SystemUserIdAccessor>();
        services.AddDbContext<IdentityDbContext>(o => o.UseNpgsql(identity).UseOpenIddict());
        services.AddOptions<ReverseSyncOptions>().Configure(o =>
        {
            o.Enabled = true;
            o.MonolithConnectionString = monolith;
            o.BatchSize = 500;
        });
        services.AddSingleton<ReverseSyncPublisher>();

        await using var provider = services.BuildServiceProvider();
        var publisher = provider.GetRequiredService<ReverseSyncPublisher>();
        var options = provider.GetRequiredService<IOptionsMonitor<ReverseSyncOptions>>().CurrentValue;

        var total = 0;
        int processed;
        do
        {
            processed = await publisher.DrainOnceAsync(options, CancellationToken.None);
            total += processed;
        } while (processed > 0);

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var pending = await db.OutboxMessages.CountAsync(m => m.PublishedAtUtc == null);
        var poisoned = await db.OutboxMessages.CountAsync(m => m.LastError != null && m.PublishedAtUtc != null);

        Console.WriteLine($"Replayed {total} outbox rows. Pending={pending} Skipped-after-max-attempts={poisoned}");
        if (poisoned > 0)
            Console.WriteLine("WARNING: some rows were skipped after exceeding MaxAttempts; inspect \"IdentityOutbox\".\"LastError\" and reconcile manually.");

        return pending == 0 && poisoned == 0 ? 0 : 2;
    }

    private sealed class SystemUserIdAccessor : IUserIdAccessor
    {
        public string? GetCurrentUserId() => "SYSTEM";
    }
}
