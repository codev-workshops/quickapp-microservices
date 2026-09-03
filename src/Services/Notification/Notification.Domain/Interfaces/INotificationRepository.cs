using Notification.Domain.Entities;

namespace Notification.Domain.Interfaces;

public interface INotificationRepository
{
    Task<OrderNotification?> GetByIdAsync(Guid id);
    Task<IReadOnlyList<OrderNotification>> GetByOrderIdAsync(Guid orderId);
    Task<IReadOnlyList<OrderNotification>> GetAllAsync(int page = 1, int pageSize = 20);
    Task<OrderNotification> AddAsync(OrderNotification notification);
    Task UpdateAsync(OrderNotification notification);
}
