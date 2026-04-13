using System.Diagnostics;
using PoCPdfSharp.Contracts;
using PoCPdfSharp.Infrastructure;
using PoCPdfSharp.Services;

namespace PoCPdfSharp.Endpoints;

public static class PdfRenderEndpoints
{
    public static IEndpointRouteBuilder MapPdfRenderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pdf").WithTags("PDF");

        group.MapPost("/render", RenderPdf)
            .Accepts<PdfRenderRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "application/pdf")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .WithName("RenderPdf")
            .WithSummary("Sanitizes HTML and renders a PDF in memory.")
            .WithDescription("Receives HTML as JSON, sanitizes it, renders it to PDF in memory, and returns the binary PDF directly.");

        return app;
    }

    private static async Task<IResult> RenderPdf(
        PdfRenderRequest request,
        IPdfRenderRequestValidator validator,
        IHtmlSanitizationService sanitizationService,
        IRemoteImageInliningService remoteImageInliningService,
        IHtmlToPdfRenderer renderer,
        HttpContext httpContext,
        ILogger<PdfRenderEndpointHandler> logger,
        CancellationToken cancellationToken)
    {
        var totalStopwatch = Stopwatch.StartNew();
        var validationElapsed = TimeSpan.Zero;
        var sanitizationElapsed = TimeSpan.Zero;
        var remoteImageInliningElapsed = TimeSpan.Zero;
        ValidatedPdfRenderRequest? validatedRequest = null;
        HtmlSanitizationResult? sanitizationResult = null;
        RemoteImageInliningResult? remoteImageInliningResult = null;
        PdfRenderResult? renderResult = null;
        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        using var requestScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["TraceIdentifier"] = httpContext.TraceIdentifier,
            ["TraceId"] = traceId
        });

        try
        {
            var validationStopwatch = Stopwatch.StartNew();
            validatedRequest = validator.Validate(request, cancellationToken);
            validationStopwatch.Stop();
            validationElapsed = validationStopwatch.Elapsed;

            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["TraceIdentifier"] = httpContext.TraceIdentifier,
                ["TraceId"] = traceId,
                ["FileName"] = validatedRequest.FileName
            });

            var sanitizationStopwatch = Stopwatch.StartNew();
            sanitizationResult = sanitizationService.Sanitize(validatedRequest, cancellationToken);
            sanitizationStopwatch.Stop();
            sanitizationElapsed = sanitizationStopwatch.Elapsed;

            if (!sanitizationResult.HasUsableContent)
            {
                throw new UnprocessableHtmlException(
                    "The sanitized HTML does not contain enough safe content to render a PDF.");
            }

            // Pipeline: sanitize HTML -> inline remote images server-side -> render PDF.
            var remoteImageInliningStopwatch = Stopwatch.StartNew();
            remoteImageInliningResult = await remoteImageInliningService.InlineAsync(
                validatedRequest,
                sanitizationResult.Html,
                cancellationToken);
            remoteImageInliningStopwatch.Stop();
            remoteImageInliningElapsed = remoteImageInliningStopwatch.Elapsed;

            renderResult = renderer.Render(validatedRequest, remoteImageInliningResult.Html, cancellationToken);
            totalStopwatch.Stop();

            if (sanitizationResult.WasAggressive)
            {
                logger.LogWarning(
                    "Aggressive sanitization detected for {FileName}. RemovedRelevantTags={RemovedRelevantTags} RemovedTagCount={RemovedTagCount} RemovedAttributeCount={RemovedAttributeCount} RemovedStyleCount={RemovedStyleCount} RemovedAtRuleCount={RemovedAtRuleCount} RemovedCommentCount={RemovedCommentCount}",
                    validatedRequest.FileName,
                    string.Join(",", sanitizationResult.RemovedRelevantTags),
                    sanitizationResult.RemovedTagCount,
                    sanitizationResult.RemovedAttributeCount,
                    sanitizationResult.RemovedStyleCount,
                    sanitizationResult.RemovedAtRuleCount,
                    sanitizationResult.RemovedCommentCount);
            }

            logger.LogInformation(
                "Rendered PDF successfully for {FileName}. OriginalHtmlLength={OriginalHtmlLength} SanitizedHtmlLength={SanitizedHtmlLength} RemoteImageProcessedCount={RemoteImageProcessedCount} RemoteImageInlinedCount={RemoteImageInlinedCount} RemoteImageFailedCount={RemoteImageFailedCount} PdfSizeBytes={PdfSizeBytes} ValidationMs={ValidationMs} SanitizationMs={SanitizationMs} RemoteImageInliningMs={RemoteImageInliningMs} ConverterPropertiesMs={ConverterPropertiesMs} RenderMs={RenderMs} ByteExtractionMs={ByteExtractionMs} TotalMs={TotalMs} WasAggressiveSanitization={WasAggressiveSanitization} RemovedRelevantTags={RemovedRelevantTags}",
                validatedRequest.FileName,
                validatedRequest.OriginalHtmlLength,
                sanitizationResult.SanitizedHtmlLength,
                remoteImageInliningResult.ProcessedResourceCount,
                remoteImageInliningResult.InlinedResourceCount,
                remoteImageInliningResult.FailedResourceCount,
                renderResult.Content.Length,
                validationElapsed.TotalMilliseconds,
                sanitizationElapsed.TotalMilliseconds,
                remoteImageInliningElapsed.TotalMilliseconds,
                renderResult.ConverterPropertiesElapsed.TotalMilliseconds,
                renderResult.RenderElapsed.TotalMilliseconds,
                renderResult.ByteExtractionElapsed.TotalMilliseconds,
                totalStopwatch.Elapsed.TotalMilliseconds,
                sanitizationResult.WasAggressive,
                string.Join(",", sanitizationResult.RemovedRelevantTags));

            return TypedResults.File(
                renderResult.Content,
                contentType: "application/pdf",
                fileDownloadName: validatedRequest.FileName);
        }
        catch (RequestValidationException exception)
        {
            totalStopwatch.Stop();

            logger.LogWarning(
                "Rejected PDF render request. Reason={Reason} FileName={FileName} OriginalHtmlLength={OriginalHtmlLength} ValidationMs={ValidationMs} TotalMs={TotalMs}",
                exception.Message,
                validatedRequest?.FileName ?? request.FileName ?? "document.pdf",
                validatedRequest?.OriginalHtmlLength ?? request.Html?.Length ?? 0,
                validationElapsed.TotalMilliseconds,
                totalStopwatch.Elapsed.TotalMilliseconds);

            exception.MarkAsLogged();
            throw;
        }
        catch (UnprocessableHtmlException exception)
        {
            totalStopwatch.Stop();

            logger.LogWarning(
                "Rejected HTML during PDF render. Reason={Reason} FileName={FileName} OriginalHtmlLength={OriginalHtmlLength} SanitizedHtmlLength={SanitizedHtmlLength} ValidationMs={ValidationMs} SanitizationMs={SanitizationMs} RemoteImageInliningMs={RemoteImageInliningMs} ConverterPropertiesMs={ConverterPropertiesMs} RenderMs={RenderMs} ByteExtractionMs={ByteExtractionMs} TotalMs={TotalMs} WasAggressiveSanitization={WasAggressiveSanitization} RemovedRelevantTags={RemovedRelevantTags}",
                exception.Message,
                validatedRequest?.FileName ?? request.FileName ?? "document.pdf",
                validatedRequest?.OriginalHtmlLength ?? request.Html?.Length ?? 0,
                sanitizationResult?.SanitizedHtmlLength ?? 0,
                validationElapsed.TotalMilliseconds,
                sanitizationElapsed.TotalMilliseconds,
                remoteImageInliningElapsed.TotalMilliseconds,
                renderResult?.ConverterPropertiesElapsed.TotalMilliseconds ?? 0,
                renderResult?.RenderElapsed.TotalMilliseconds ?? 0,
                renderResult?.ByteExtractionElapsed.TotalMilliseconds ?? 0,
                totalStopwatch.Elapsed.TotalMilliseconds,
                sanitizationResult?.WasAggressive ?? false,
                sanitizationResult is null ? string.Empty : string.Join(",", sanitizationResult.RemovedRelevantTags));

            exception.MarkAsLogged();
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            totalStopwatch.Stop();

            logger.LogError(
                exception,
                "Failed to render PDF unexpectedly. FileName={FileName} OriginalHtmlLength={OriginalHtmlLength} SanitizedHtmlLength={SanitizedHtmlLength} PdfSizeBytes={PdfSizeBytes} ValidationMs={ValidationMs} SanitizationMs={SanitizationMs} RemoteImageInliningMs={RemoteImageInliningMs} ConverterPropertiesMs={ConverterPropertiesMs} RenderMs={RenderMs} ByteExtractionMs={ByteExtractionMs} TotalMs={TotalMs} WasAggressiveSanitization={WasAggressiveSanitization} RemovedRelevantTags={RemovedRelevantTags}",
                validatedRequest?.FileName ?? "document.pdf",
                validatedRequest?.OriginalHtmlLength ?? request.Html?.Length ?? 0,
                sanitizationResult?.SanitizedHtmlLength ?? 0,
                renderResult?.Content.Length ?? 0,
                validationElapsed.TotalMilliseconds,
                sanitizationElapsed.TotalMilliseconds,
                remoteImageInliningElapsed.TotalMilliseconds,
                renderResult?.ConverterPropertiesElapsed.TotalMilliseconds ?? 0,
                renderResult?.RenderElapsed.TotalMilliseconds ?? 0,
                renderResult?.ByteExtractionElapsed.TotalMilliseconds ?? 0,
                totalStopwatch.Elapsed.TotalMilliseconds,
                sanitizationResult?.WasAggressive ?? false,
                sanitizationResult is null ? string.Empty : string.Join(",", sanitizationResult.RemovedRelevantTags));

            exception.MarkAsLogged();
            throw;
        }
    }

    private sealed class PdfRenderEndpointHandler;
}
