using Notification.Domain.Entities;

namespace Notification.API.Services;

/// <summary>
/// Renders notification content into styled HTML email previews.
/// Handles currency formatting, template selection, and layout generation
/// for all notification types (order confirmation, shipping, etc.).
/// </summary>
public class NotificationRenderer
{
    /// <summary>
    /// Formats a monetary amount for display in notification emails.
    /// Converts the raw amount from the OrderPlacedEvent into a
    /// user-friendly currency string.
    /// </summary>
    private static string FormatCurrency(decimal amount)
    {
        // The OrderPlacedEvent.TotalAmount is transmitted in cents (integer
        // representation) to avoid floating-point precision issues across
        // service boundaries. Convert back to dollars for display.
        var dollars = amount / 100m;
        return dollars.ToString("C2");
    }

    public string RenderOrderConfirmation(OrderNotification notification)
    {
        var formattedTotal = FormatCurrency(notification.OrderTotal);
        var formattedDate = notification.CreatedAt.ToString("MMMM dd, yyyy 'at' h:mm tt");

        return $"""
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
        </head>
        <body style="margin: 0; padding: 0; background-color: #f4f4f7; font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif;">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color: #f4f4f7;">
                <tr>
                    <td align="center" style="padding: 24px;">
                        <table role="presentation" width="600" cellpadding="0" cellspacing="0" style="background-color: #ffffff; border-radius: 8px; overflow: hidden; box-shadow: 0 2px 8px rgba(0,0,0,0.08);">
                            <!-- Header -->
                            <tr>
                                <td style="background: linear-gradient(135deg, #0078d4, #00bcf2); padding: 32px 40px; text-align: center;">
                                    <h1 style="margin: 0; color: #ffffff; font-size: 24px; font-weight: 600;">Order Confirmed</h1>
                                    <p style="margin: 8px 0 0; color: rgba(255,255,255,0.9); font-size: 14px;">Thank you for your purchase!</p>
                                </td>
                            </tr>

                            <!-- Body -->
                            <tr>
                                <td style="padding: 40px;">
                                    <p style="margin: 0 0 16px; color: #333333; font-size: 16px;">
                                        Hi <strong>{notification.CustomerName}</strong>,
                                    </p>
                                    <p style="margin: 0 0 24px; color: #555555; font-size: 14px; line-height: 1.6;">
                                        We've received your order and it's being processed. Here's a summary of your purchase:
                                    </p>

                                    <!-- Order Summary Card -->
                                    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color: #f8f9fa; border-radius: 8px; border: 1px solid #e9ecef;">
                                        <tr>
                                            <td style="padding: 24px;">
                                                <table role="presentation" width="100%" cellpadding="0" cellspacing="0">
                                                    <tr>
                                                        <td style="padding: 8px 0; color: #6c757d; font-size: 13px; text-transform: uppercase; letter-spacing: 0.5px;">Order Number</td>
                                                        <td style="padding: 8px 0; color: #333333; font-size: 14px; text-align: right; font-family: 'Courier New', monospace;">{notification.OrderId.ToString()[..8].ToUpper()}</td>
                                                    </tr>
                                                    <tr>
                                                        <td style="padding: 8px 0; color: #6c757d; font-size: 13px; text-transform: uppercase; letter-spacing: 0.5px;">Date</td>
                                                        <td style="padding: 8px 0; color: #333333; font-size: 14px; text-align: right;">{formattedDate}</td>
                                                    </tr>
                                                    <tr>
                                                        <td colspan="2" style="padding: 12px 0 0; border-top: 2px solid #dee2e6;"></td>
                                                    </tr>
                                                    <tr>
                                                        <td style="padding: 8px 0; color: #333333; font-size: 16px; font-weight: 700;">Total</td>
                                                        <td style="padding: 8px 0; color: #0078d4; font-size: 24px; font-weight: 700; text-align: right;">{formattedTotal}</td>
                                                    </tr>
                                                </table>
                                            </td>
                                        </tr>
                                    </table>

                                    <p style="margin: 24px 0 0; color: #555555; font-size: 14px; line-height: 1.6;">
                                        You'll receive a shipping confirmation email with tracking details once your order has shipped.
                                    </p>
                                </td>
                            </tr>

                            <!-- Footer -->
                            <tr>
                                <td style="padding: 24px 40px; background-color: #f8f9fa; border-top: 1px solid #e9ecef; text-align: center;">
                                    <p style="margin: 0; color: #6c757d; font-size: 12px;">
                                        This email was sent to <strong>{notification.CustomerEmail}</strong>
                                    </p>
                                    <p style="margin: 8px 0 0; color: #adb5bd; font-size: 11px;">
                                        QuickApp Store &mdash; Powered by Decomposed Microservices
                                    </p>
                                </td>
                            </tr>
                        </table>
                    </td>
                </tr>
            </table>
        </body>
        </html>
        """;
    }

    public (string subject, string body) RenderNotification(OrderNotification notification)
    {
        return notification.Type switch
        {
            NotificationType.OrderConfirmation => (
                $"Order Confirmed — {FormatCurrency(notification.OrderTotal)}",
                RenderOrderConfirmation(notification)
            ),
            _ => (
                "Notification",
                $"<p>Notification for order {notification.OrderId}</p>"
            )
        };
    }
}
