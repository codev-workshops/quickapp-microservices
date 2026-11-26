# Order confirmation email shows the wrong amount

## Symptom and reproduction

An order submitted with a total of `149.99` was stored as `149.99`, but its confirmation email subject and HTML preview displayed `$1.50` instead of `$149.99`.

Run Notification.API on port 5005 with a local PostgreSQL instance and an `en-US` locale so currency displays in dollars. Submit the lab event and open the URL returned in `previewUrl`:

```sh
curl -fsS http://localhost:5005/api/notification/events/order-placed \
  -H 'Content-Type: application/json' \
  -d '{"orderId":"11111111-1111-1111-1111-111111111111","customerId":"22222222-2222-2222-2222-222222222222","totalAmount":149.99,"placedAt":"2026-03-17T12:00:00Z"}'
```

## Data flow and root cause

1. [NotificationController.ReceiveOrderPlacedEvent](src/Services/Notification/Notification.API/Controllers/NotificationController.cs#L90-L104) copies `totalAmount` into the shared [OrderPlacedEvent](src/Shared/Shared.Contracts/Events/OrderPlacedEvent.cs#L7-L12) without converting it. The shared field is a `decimal`; its declaration does not specify a unit. For the lab event, `149.99` already represents dollars, not cents.
2. [OrderEventConsumer.HandleOrderPlaced](src/Services/Notification/Notification.API/Services/OrderEventConsumer.cs#L28-L52) receives `149.99` and assigns it unchanged to `OrderTotal`. Its log and the notification detail endpoint both showed `149.99` before the fix.
3. [NotificationRenderer.FormatCurrency at the original line 22](https://github.com/codev-workshops/quickapp-microservices/blob/5557c985f190bd17f5c62ac6af2765bba00035ee/src/Services/Notification/Notification.API/Services/NotificationRenderer.cs#L17-L23) incorrectly assumed the amount was in cents and divided by 100. That turned `149.99` into `1.4999`, which the two-decimal currency format rounded to `$1.50`. Both the email subject and body used this formatter. The [preview endpoint](src/Services/Notification/Notification.API/Controllers/NotificationController.cs#L71-L82) returned the stored HTML without doing any further formatting.

## Fix and scope

[FormatCurrency](src/Services/Notification/Notification.API/Services/NotificationRenderer.cs#L12-L18) now formats the amount as received, retaining the existing culture-sensitive display format. No event payload or database schema changes were needed. The notification project's shared-project references were also corrected so the service builds against the intended shared contract.

No other service in this checkout consumes `OrderPlacedEvent.TotalAmount`, and the other notification types do not format an amount. The Order service is scaffolding and does not publish this event here; the verified path was the local HTTP endpoint, not RabbitMQ. Existing notifications retain their stored subject and HTML; the fix applies to newly rendered notifications.

## Verification

- Repeated the same POST against PostgreSQL-backed Notification.API: the stored `orderTotal` was `149.99` both times, and the subject and browser preview changed from `$1.50` to `$149.99`.
- `dotnet test src/Services/Notification/Notification.Tests --nologo`: 1 passed, 0 failed. The regression test checks the dollar amount in the subject and HTML body.
- Chrome screenshots on the lab machine: `~/b2-shots/before.png` (`$1.50`) and `~/b2-shots/after.png` (`$149.99`). The screenshots are not committed.
