using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Notification.API.Services;
using Notification.Domain.Entities;
using Notification.Domain.Interfaces;
using Shared.Contracts.Events;

namespace Notification.Tests;

public class OrderEventConsumerTests
{
    private readonly InMemoryNotificationRepository _repository = new();
    private readonly NotificationRenderer _renderer = new();
    private readonly OrderEventConsumer _consumer;

    public OrderEventConsumerTests()
    {
        _consumer = new OrderEventConsumer(
            _repository,
            _renderer,
            NullLogger<OrderEventConsumer>.Instance);
    }

    [Fact]
    public async Task HandleOrderPlaced_CreatesNotification()
    {
        var orderEvent = new OrderPlacedEvent(
            Guid.NewGuid(), Guid.NewGuid(), 5000m, DateTime.UtcNow);

        var result = await _consumer.HandleOrderPlaced(orderEvent);

        Assert.NotNull(result);
        Assert.Equal(orderEvent.OrderId, result.OrderId);
        Assert.Equal(orderEvent.CustomerId, result.CustomerId);
        Assert.Equal(orderEvent.TotalAmount, result.OrderTotal);
    }

    [Fact]
    public async Task HandleOrderPlaced_SetsStatusToRendered()
    {
        var orderEvent = new OrderPlacedEvent(
            Guid.NewGuid(), Guid.NewGuid(), 2500m, DateTime.UtcNow);

        var result = await _consumer.HandleOrderPlaced(orderEvent);

        Assert.Equal(NotificationStatus.Rendered, result.Status);
    }

    [Fact]
    public async Task HandleOrderPlaced_RendersSubjectAndBody()
    {
        var orderEvent = new OrderPlacedEvent(
            Guid.NewGuid(), Guid.NewGuid(), 10000m, DateTime.UtcNow);

        var result = await _consumer.HandleOrderPlaced(orderEvent);

        Assert.False(string.IsNullOrWhiteSpace(result.RenderedSubject));
        Assert.False(string.IsNullOrWhiteSpace(result.RenderedBody));
    }

    [Fact]
    public async Task HandleOrderPlaced_PersistsToRepository()
    {
        var orderEvent = new OrderPlacedEvent(
            Guid.NewGuid(), Guid.NewGuid(), 7500m, DateTime.UtcNow);

        var result = await _consumer.HandleOrderPlaced(orderEvent);

        var stored = await _repository.GetByIdAsync(result.Id);
        Assert.NotNull(stored);
        Assert.Equal(result.OrderId, stored.OrderId);
    }
}

internal class InMemoryNotificationRepository : INotificationRepository
{
    private readonly List<OrderNotification> _notifications = new();

    public Task<OrderNotification?> GetByIdAsync(Guid id)
        => Task.FromResult(_notifications.FirstOrDefault(n => n.Id == id));

    public Task<IReadOnlyList<OrderNotification>> GetByOrderIdAsync(Guid orderId)
        => Task.FromResult<IReadOnlyList<OrderNotification>>(
            _notifications.Where(n => n.OrderId == orderId).ToList());

    public Task<IReadOnlyList<OrderNotification>> GetAllAsync(int page = 1, int pageSize = 20)
        => Task.FromResult<IReadOnlyList<OrderNotification>>(
            _notifications.Skip((page - 1) * pageSize).Take(pageSize).ToList());

    public Task<OrderNotification> AddAsync(OrderNotification notification)
    {
        _notifications.Add(notification);
        return Task.FromResult(notification);
    }

    public Task UpdateAsync(OrderNotification notification) => Task.CompletedTask;
}
