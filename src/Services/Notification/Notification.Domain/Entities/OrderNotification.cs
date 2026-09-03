namespace Notification.Domain.Entities;

public class OrderNotification
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal OrderTotal { get; set; }
    public string CustomerEmail { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public NotificationType Type { get; set; }
    public NotificationStatus Status { get; set; }
    public string? RenderedSubject { get; set; }
    public string? RenderedBody { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}

public enum NotificationType
{
    OrderConfirmation,
    OrderShipped,
    OrderDelivered,
    OrderCancelled
}

public enum NotificationStatus
{
    Pending,
    Rendered,
    Sent,
    Failed
}
