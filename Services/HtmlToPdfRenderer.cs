using System.Diagnostics;
using iText.Html2pdf;
using iText.Kernel.Pdf;
using Microsoft.Extensions.Options;
using PoCPdfSharp.Contracts;
using PoCPdfSharp.Infrastructure;
using PoCPdfSharp.Options;

namespace PoCPdfSharp.Services;

public sealed class HtmlToPdfRenderer
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<PdfRenderingOptions> _options;
    private readonly ILoggerFactory _loggerFactory;

    public HtmlToPdfRenderer(
        IHttpClientFactory httpClientFactory,
        IOptions<PdfRenderingOptions> options,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _loggerFactory = loggerFactory;
    }

    public PdfRenderResult Render(
        ValidatedPdfRenderRequest request,
        string sanitizedHtml,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var converterPropertiesStopwatch = Stopwatch.StartNew();
        var converterProperties = BuildConverterProperties(request, cancellationToken);
        converterPropertiesStopwatch.Stop();

        using var output = new MemoryStream();

        // Forcing GC.Collect() per request would increase latency and reduce throughput.
        // Deterministic disposal and not retaining large objects beyond the request are the correct controls here.
        var renderStopwatch = Stopwatch.StartNew();
        using (var writer = new PdfWriter(output))
        using (var pdfDocument = new PdfDocument(writer))
        {
            HtmlConverter.ConvertToPdf(sanitizedHtml, pdfDocument, converterProperties);
        }
        renderStopwatch.Stop();

        cancellationToken.ThrowIfCancellationRequested();

        var byteExtractionStopwatch = Stopwatch.StartNew();
        var pdfBytes = output.ToArray();
        byteExtractionStopwatch.Stop();

        return new PdfRenderResult(
            pdfBytes,
            converterPropertiesStopwatch.Elapsed,
            renderStopwatch.Elapsed,
            byteExtractionStopwatch.Elapsed);
    }

    private ConverterProperties BuildConverterProperties(
        ValidatedPdfRenderRequest request,
        CancellationToken cancellationToken)
    {
        var resourceRetriever = new RestrictedResourceRetriever(
            _httpClientFactory,
            _options,
            _loggerFactory.CreateLogger<RestrictedResourceRetriever>(),
            request,
            cancellationToken);

        var converterProperties = new ConverterProperties()
            .SetCharset("utf-8")
            .SetLimitOfLayouts(_options.Value.MaxLayoutPasses)
            .SetResourceRetriever(resourceRetriever);

        if (request.BaseUri is not null)
        {
            converterProperties.SetBaseUri(request.BaseUri.AbsoluteUri);
        }

        return converterProperties;
    }
}
