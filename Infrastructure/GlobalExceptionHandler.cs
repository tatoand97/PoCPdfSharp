using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace PoCPdfSharp.Infrastructure;

public sealed class GlobalExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
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

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = exception.Message,
            Type = type,
            Instance = httpContext.Request.Path
        };

        httpContext.Response.StatusCode = statusCode;

        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = problemDetails
        });

        return true;
    }
}
