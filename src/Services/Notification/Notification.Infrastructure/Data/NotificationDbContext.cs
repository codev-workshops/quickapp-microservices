using Microsoft.EntityFrameworkCore;
using Notification.Domain.Entities;

namespace Notification.Infrastructure.Data;

public class NotificationDbContext : DbContext
{
    public NotificationDbContext(DbContextOptions<NotificationDbContext> options) : base(options)
    {
    }

    public DbSet<OrderNotification> OrderNotifications => Set<OrderNotification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OrderNotification>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OrderTotal).HasColumnType("decimal(18,2)");
            entity.Property(e => e.CustomerEmail).HasMaxLength(256);
            entity.Property(e => e.CustomerName).HasMaxLength(256);
            entity.Property(e => e.RenderedSubject).HasMaxLength(512);
            entity.HasIndex(e => e.OrderId);
            entity.HasIndex(e => e.CustomerId);
        });
    }
}
