namespace Shared.Contracts.Events;

/// <summary>
/// Integration event published when a new order is placed.
/// Consumed by Notification service to trigger confirmation emails.
/// </summary>
public record OrderPlacedEvent(
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    DateTime PlacedAt
);
