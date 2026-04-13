using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace PoCPdfSharp.Infrastructure;

public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var (statusCode, title, type) = exception switch
        {
            RequestValidationException => (
                StatusCodes.Status400BadRequest,
                "The PDF render request is invalid.",
                "https://httpstatuses.com/400"),
            UnprocessableHtmlException => (
                StatusCodes.Status422UnprocessableEntity,
                "The sanitized HTML cannot be rendered.",
                "https://httpstatuses.com/422"),
            BadHttpRequestException => (
                StatusCodes.Status400BadRequest,
                "The HTTP request is invalid.",
                "https://httpstatuses.com/400"),
            _ => (
                StatusCodes.Status500InternalServerError,
                "An unexpected error occurred while rendering the PDF.",
                "https://httpstatuses.com/500")
        };

        var detail = exception switch
        {
            RequestValidationException or UnprocessableHtmlException or BadHttpRequestException => exception.Message,
            _ => "The PDF render request failed unexpectedly. Use the traceId value to correlate server-side diagnostics."
        };

        LogControlledException(httpContext, exception, statusCode);

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = type,
            Instance = httpContext.Request.Path
        };
        problemDetails.Extensions["traceId"] =
            Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = statusCode;
        try
        {
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = problemDetails
            });
        }
        catch (InvalidOperationException) when (!httpContext.Response.HasStarted)
        {
            await WriteFallbackProblemDetailsAsync(httpContext, problemDetails, cancellationToken);
        }

        return true;
    }

    private static Task WriteFallbackProblemDetailsAsync(
        HttpContext httpContext,
        ProblemDetails problemDetails,
        CancellationToken cancellationToken)
    {
        httpContext.Response.ContentType = "application/problem+json";

        return httpContext.Response.WriteAsJsonAsync(
            problemDetails,
            JsonSerializerOptions.Web,
            contentType: "application/problem+json",
            cancellationToken: cancellationToken);
    }

    private void LogControlledException(HttpContext httpContext, Exception exception, int statusCode)
    {
        if (exception.HasBeenLogged())
        {
            return;
        }

        if (exception is not (RequestValidationException or UnprocessableHtmlException or BadHttpRequestException))
        {
            return;
        }

        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        logger.LogWarning(
            "Handled {ExceptionType} as {StatusCode}. Method={RequestMethod} Path={RequestPath} Reason={Reason} TraceIdentifier={TraceIdentifier} TraceId={TraceId}",
            exception.GetType().Name,
            statusCode,
            httpContext.Request.Method,
            httpContext.Request.Path,
            exception.Message,
            httpContext.TraceIdentifier,
            traceId);

        exception.MarkAsLogged();
    }
}
