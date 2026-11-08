using System.Text.Json;
using FluentValidation;
using LMS.Domain.Exceptions;

namespace LMS.WebApi.Middleware;

/// <summary>
/// Catches every unhandled exception, maps it to a stable JSON envelope with a
/// trace ID, and logs the original throw via Serilog so operators can grep.
/// Production responses never contain <c>ex.Message</c> — that's how PII and
/// connection strings leak. Dev responses surface the message to make debugging
/// painless.
/// </summary>
public sealed class GlobalExceptionMiddleware(
    RequestDelegate next,
    ILogger<GlobalExceptionMiddleware> logger,
    IHostEnvironment env)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task Invoke(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ValidationException ex)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, new
            {
                message = "Validation failed",
                traceId = context.TraceIdentifier,
                errors = ex.Errors.Select(e => new { e.PropertyName, e.ErrorMessage })
            });
        }
        catch (DomainException ex)
        {
            // A broken domain invariant is the caller's mistake, not a server
            // fault — it must read as a 400 with the actual rule that failed
            // ("Only a draft assignment can be published."), not a generic 500
            // "Internal server error." Most handlers use the Result pattern, but
            // any domain method that throws straight through (and isn't caught in
            // the handler) would otherwise surface as a scary 500 to the user.
            // DomainException messages are curated, user-safe validation text, so
            // they're returned verbatim in every environment.
            logger.LogInformation(ex,
                "Domain rule rejected. TraceId={TraceId} Path={Path}",
                context.TraceIdentifier, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status400BadRequest, new
            {
                message = ex.Message,
                traceId = context.TraceIdentifier,
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex,
                "Authorization failed. TraceId={TraceId} Path={Path}",
                context.TraceIdentifier, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status403Forbidden, new
            {
                message = "Forbidden.",
                traceId = context.TraceIdentifier,
            });
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogInformation(ex,
                "Resource not found. TraceId={TraceId} Path={Path}",
                context.TraceIdentifier, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status404NotFound, new
            {
                message = "Not found.",
                traceId = context.TraceIdentifier,
            });
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away — don't log as error, don't write a body
            // (the connection is gone). Caller's cancellation token surfaces
            // here from any async wait.
            logger.LogDebug("Request aborted by client. Path={Path}", context.Request.Path);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Unhandled exception. TraceId={TraceId} Path={Path} Method={Method}",
                context.TraceIdentifier, context.Request.Path, context.Request.Method);

            await WriteAsync(context, StatusCodes.Status500InternalServerError, new
            {
                message = env.IsDevelopment() ? ex.Message : "Internal server error.",
                traceId = context.TraceIdentifier,
            });
        }
    }

    private static Task WriteAsync(HttpContext ctx, int status, object payload)
    {
        // Don't try to write a body if the response has already started — the
        // framework will throw "Headers are read-only" otherwise.
        if (ctx.Response.HasStarted) return Task.CompletedTask;

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
    }
}
