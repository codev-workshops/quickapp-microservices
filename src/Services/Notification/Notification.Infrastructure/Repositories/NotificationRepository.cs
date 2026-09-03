using Microsoft.EntityFrameworkCore;
using Notification.Domain.Entities;
using Notification.Domain.Interfaces;
using Notification.Infrastructure.Data;

namespace Notification.Infrastructure.Repositories;

public class NotificationRepository : INotificationRepository
{
    private readonly NotificationDbContext _context;

    public NotificationRepository(NotificationDbContext context)
    {
        _context = context;
    }

    public async Task<OrderNotification?> GetByIdAsync(Guid id)
    {
        return await _context.OrderNotifications.FindAsync(id);
    }

    public async Task<IReadOnlyList<OrderNotification>> GetByOrderIdAsync(Guid orderId)
    {
        return await _context.OrderNotifications
            .Where(n => n.OrderId == orderId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<OrderNotification>> GetAllAsync(int page = 1, int pageSize = 20)
    {
        return await _context.OrderNotifications
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();
    }

    public async Task<OrderNotification> AddAsync(OrderNotification notification)
    {
        _context.OrderNotifications.Add(notification);
        await _context.SaveChangesAsync();
        return notification;
    }

    public async Task UpdateAsync(OrderNotification notification)
    {
        _context.OrderNotifications.Update(notification);
        await _context.SaveChangesAsync();
    }
}
