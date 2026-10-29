using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServiceDesk.Domain;

namespace ServiceDesk.Services;

/// <summary>
/// Assigns every request a short correlation id (a caller-supplied value is honored when it is a safe
/// ASCII token) and turns failures into consistent Problem responses that carry the same id. Error logs
/// record the id, the method and the path — never request bodies, cookies or headers.
/// </summary>
public static class HttpCorrelation
{
    private const string NotReadyTitle = "The service is not ready: its database is unreachable or the schema is out of date.";

    public static async Task Run(HttpContext context, RequestDelegate next, ILogger logger)
    {
        var id = Accept(context.Request.Headers["X-Correlation-ID"]) ?? Guid.NewGuid().ToString("N")[..12];
        context.TraceIdentifier = id;
        context.Response.OnStarting(() => { context.Response.Headers["X-Correlation-ID"] = id; return Task.CompletedTask; });
        try { await next(context); }
        catch (RuleException ex) { await Write(context, ex.Status, ex.Message); }
        catch (DbUpdateConcurrencyException) { await Write(context, 409, "The record changed. Refresh and try again."); }
        catch (DbUpdateException ex)
        {
            logger.LogWarning(ex, "Database mutation rejected {CorrelationId}", id);
            await Write(context, 409, "The change conflicts with stored data. Refresh and check your input.");
        }
        catch (AntiforgeryValidationException) { await Write(context, 400, "Invalid security token. Refresh the page."); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled request failure {CorrelationId} {Method} {Path}", id, context.Request.Method, context.Request.Path);
            await Write(context, 500, "An unexpected server error occurred.");
        }
    }

    private static string? Accept(string? supplied) =>
        string.IsNullOrEmpty(supplied) || supplied.Length < 8 || supplied.Length > 64
            || !supplied.All(char.IsAsciiLetterOrDigit) ? null : supplied;

    private static async Task Write(HttpContext context, int status, string title) =>
        await Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["correlationId"] = context.TraceIdentifier }).ExecuteAsync(context);

    public static IResult NotReady() => Results.Problem(statusCode: 503, title: NotReadyTitle);
}
