using System.Text.RegularExpressions;
using Desk.Infrastructure.Messaging;
using Serilog.Context;

namespace Desk.Api.Http;

/// <summary>
/// Gives every request a correlation id (the caller's <c>X-Correlation-Id</c> when it is a sane token, otherwise a new one),
/// echoes it in the response and puts it in the log context. Events written to the outbox during the request carry it,
/// so one id follows the request through RabbitMQ and the Worker logs.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex Safe();

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[HeaderName].FirstOrDefault();
        var id = supplied is not null && Safe().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");

        context.RequestServices.GetRequiredService<CorrelationContext>().Id = id;
        context.Response.Headers[HeaderName] = id;
        using (LogContext.PushProperty("CorrelationId", id))
        {
            await next(context);
        }
    }
}
