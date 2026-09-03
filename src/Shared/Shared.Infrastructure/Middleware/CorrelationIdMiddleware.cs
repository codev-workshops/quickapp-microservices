using Microsoft.AspNetCore.Http;

namespace Shared.Infrastructure.Middleware;

/// <summary>
/// Propagates a correlation ID header across microservice boundaries
/// for distributed tracing in Kubernetes.
/// </summary>
public class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-ID";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey(CorrelationIdHeader))
        {
            context.Request.Headers[CorrelationIdHeader] = Guid.NewGuid().ToString();
        }

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationIdHeader] =
                context.Request.Headers[CorrelationIdHeader].ToString();
            return Task.CompletedTask;
        });

        await _next(context);
    }
}
