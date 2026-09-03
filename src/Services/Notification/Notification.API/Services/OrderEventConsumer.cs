using Notification.Domain.Entities;
using Notification.Domain.Interfaces;
using Shared.Contracts.Events;

namespace Notification.API.Services;

/// <summary>
/// Consumes OrderPlacedEvent messages from the Order service (via RabbitMQ)
/// and creates notification records. In the current local-testing configuration,
/// this is also exposed as an HTTP endpoint for synchronous event ingestion.
/// </summary>
public class OrderEventConsumer
{
    private readonly INotificationRepository _repository;
    private readonly NotificationRenderer _renderer;
    private readonly ILogger<OrderEventConsumer> _logger;

    public OrderEventConsumer(
        INotificationRepository repository,
        NotificationRenderer renderer,
        ILogger<OrderEventConsumer> logger)
    {
        _repository = repository;
        _renderer = renderer;
        _logger = logger;
    }

    public async Task<OrderNotification> HandleOrderPlaced(OrderPlacedEvent orderEvent)
    {
        _logger.LogInformation(
            "Processing OrderPlacedEvent for Order {OrderId}, Customer {CustomerId}, Amount {TotalAmount}",
            orderEvent.OrderId, orderEvent.CustomerId, orderEvent.TotalAmount);

        var notification = new OrderNotification
        {
            Id = Guid.NewGuid(),
            OrderId = orderEvent.OrderId,
            CustomerId = orderEvent.CustomerId,
            OrderTotal = orderEvent.TotalAmount,
            CustomerEmail = "customer@example.com",
            CustomerName = "Valued Customer",
            Type = NotificationType.OrderConfirmation,
            Status = NotificationStatus.Pending,
            CreatedAt = DateTime.UtcNow
        };

        var (subject, body) = _renderer.RenderNotification(notification);
        notification.RenderedSubject = subject;
        notification.RenderedBody = body;
        notification.Status = NotificationStatus.Rendered;

        await _repository.AddAsync(notification);

        _logger.LogInformation(
            "Notification {NotificationId} rendered for Order {OrderId}",
            notification.Id, notification.OrderId);

        return notification;
    }
}
