using Microsoft.AspNetCore.Mvc;
using Notification.API.Services;
using Notification.Domain.Interfaces;
using Shared.Contracts.Events;

namespace Notification.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NotificationController : ControllerBase
{
    private readonly INotificationRepository _repository;
    private readonly OrderEventConsumer _eventConsumer;
    private readonly ILogger<NotificationController> _logger;

    public NotificationController(
        INotificationRepository repository,
        OrderEventConsumer eventConsumer,
        ILogger<NotificationController> logger)
    {
        _repository = repository;
        _eventConsumer = eventConsumer;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var notifications = await _repository.GetAllAsync(page, pageSize);
        return Ok(notifications.Select(n => new
        {
            n.Id,
            n.OrderId,
            n.CustomerId,
            n.OrderTotal,
            n.Type,
            n.Status,
            n.RenderedSubject,
            n.CreatedAt,
            n.SentAt
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var notification = await _repository.GetByIdAsync(id);
        if (notification is null)
            return NotFound();

        return Ok(new
        {
            notification.Id,
            notification.OrderId,
            notification.CustomerId,
            notification.OrderTotal,
            notification.CustomerEmail,
            notification.CustomerName,
            notification.Type,
            notification.Status,
            notification.RenderedSubject,
            notification.CreatedAt,
            notification.SentAt
        });
    }

    /// <summary>
    /// Returns the rendered HTML email preview for a notification.
    /// Use this endpoint to visually inspect notification output.
    /// </summary>
    [HttpGet("{id:guid}/preview")]
    [Produces("text/html")]
    public async Task<IActionResult> GetPreview(Guid id)
    {
        var notification = await _repository.GetByIdAsync(id);
        if (notification is null)
            return NotFound();

        if (string.IsNullOrEmpty(notification.RenderedBody))
            return NotFound("Notification has not been rendered yet.");

        return Content(notification.RenderedBody, "text/html");
    }

    /// <summary>
    /// HTTP endpoint for receiving OrderPlacedEvent messages.
    /// In production this would be a RabbitMQ consumer; this endpoint
    /// enables local testing without a message broker.
    /// </summary>
    [HttpPost("events/order-placed")]
    public async Task<IActionResult> ReceiveOrderPlacedEvent([FromBody] OrderPlacedEventDto dto)
    {
        var orderEvent = new OrderPlacedEvent(
            dto.OrderId,
            dto.CustomerId,
            dto.TotalAmount,
            dto.PlacedAt);

        var notification = await _eventConsumer.HandleOrderPlaced(orderEvent);

        return CreatedAtAction(
            nameof(GetPreview),
            new { id = notification.Id },
            new { notification.Id, PreviewUrl = $"/api/notification/{notification.Id}/preview" });
    }
}

/// <summary>
/// DTO for receiving order events via HTTP (local testing).
/// Maps to the shared OrderPlacedEvent contract.
/// </summary>
public record OrderPlacedEventDto(
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    DateTime PlacedAt);
