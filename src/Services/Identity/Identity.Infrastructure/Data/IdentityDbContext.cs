using Identity.Domain.Entities;
using Identity.Domain.Interfaces;
using Identity.Infrastructure.Outbox;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.Json;

namespace Identity.Infrastructure.Data;

public class IdentityDbContext(DbContextOptions<IdentityDbContext> options, IUserIdAccessor userIdAccessor) :
    IdentityDbContext<ApplicationUser, ApplicationRole, string>(options)
{
    public DbSet<IdentityOutboxMessage> OutboxMessages => Set<IdentityOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>()
            .HasMany(u => u.Claims)
            .WithOne()
            .HasForeignKey(c => c.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ApplicationUser>()
            .HasMany(u => u.Roles)
            .WithOne()
            .HasForeignKey(r => r.UserId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ApplicationRole>()
            .HasMany(r => r.Claims)
            .WithOne()
            .HasForeignKey(c => c.RoleId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ApplicationRole>()
            .HasMany(r => r.Users)
            .WithOne()
            .HasForeignKey(r => r.RoleId)
            .IsRequired()
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<IdentityOutboxMessage>(e =>
        {
            e.ToTable("IdentityOutbox");
            e.HasKey(m => m.Id);
            e.Property(m => m.TableName).HasMaxLength(128).IsRequired();
            e.Property(m => m.KeyJson).IsRequired();
            e.HasIndex(m => m.PublishedAtUtc);
        });

        builder.UseOpenIddict();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        return SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AddAuditInfo();

        var tracked = ChangeTracker.Entries()
            .Where(e => e.Entity is not IdentityOutboxMessage &&
                        e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(e => (Entry: e, e.State))
            .ToList();

        if (tracked.Count == 0)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        var ownsTransaction = Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await Database.BeginTransactionAsync(cancellationToken) : null;

        try
        {
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

            var now = DateTime.UtcNow;
            foreach (var (entry, state) in tracked)
                OutboxMessages.Add(CreateOutboxMessage(entry, state, now));

            await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);

            return result;
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    private void AddAuditInfo()
    {
        var currentUserId = userIdAccessor.GetCurrentUserId();
        var now = DateTime.UtcNow;

        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>()
                     .Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedDate = now;
                entry.Entity.CreatedBy = currentUserId;
            }

            entry.Entity.UpdatedDate = now;
            entry.Entity.UpdatedBy = currentUserId;
        }
    }

    private static IdentityOutboxMessage CreateOutboxMessage(EntityEntry entry, EntityState state, DateTime now)
    {
        var entityType = entry.Metadata;
        var tableName = entityType.GetTableName() ?? entityType.ShortName();
        var table = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());

        var key = new Dictionary<string, object?>();
        foreach (var keyProperty in entityType.FindPrimaryKey()!.Properties)
            key[keyProperty.GetColumnName(table) ?? keyProperty.Name] = entry.Property(keyProperty.Name).CurrentValue;

        Dictionary<string, object?>? payload = null;
        if (state != EntityState.Deleted)
        {
            payload = [];
            foreach (var property in entityType.GetProperties())
            {
                var column = property.GetColumnName(table);
                if (column is null)
                    continue;

                payload[column] = entry.Property(property.Name).CurrentValue;
            }
        }

        return new IdentityOutboxMessage
        {
            TableName = tableName,
            Operation = state == EntityState.Deleted ? OutboxOperation.Delete : OutboxOperation.Upsert,
            KeyJson = JsonSerializer.Serialize(key),
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload),
            OccurredAtUtc = now
        };
    }
}
