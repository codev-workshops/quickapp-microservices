namespace Shared.Contracts.Events;

/// <summary>
/// Integration event published when a new order is placed.
/// Consumed by Notification service to trigger confirmation emails.
/// </summary>
/// <param name="TotalAmount">Order total in major currency units (e.g. 149.99 = $149.99), not cents.</param>
public record OrderPlacedEvent(
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    DateTime PlacedAt
);
