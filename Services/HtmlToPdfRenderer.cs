using System.Diagnostics;
using AngleSharp.Html.Parser;
using iText.Html2pdf;
using iText.Kernel.Pdf;
using Microsoft.Extensions.Options;
using PoCPdfSharp.Contracts;
using PoCPdfSharp.Infrastructure;
using PoCPdfSharp.Options;

namespace PoCPdfSharp.Services;

public sealed class HtmlToPdfRenderer : IHtmlToPdfRenderer
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<PdfRenderingOptions> _options;

    public HtmlToPdfRenderer(
        IHttpClientFactory httpClientFactory,
        IOptions<PdfRenderingOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public PdfRenderResult Render(
        ValidatedPdfRenderRequest request,
        string sanitizedHtml,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var converterPropertiesStopwatch = Stopwatch.StartNew();
        var resourceRetriever = CreateResourceRetriever(request, cancellationToken);

        // pdfHTML may silently omit rejected resources depending on the element type.
        // Preflighting sanitized resource URLs keeps the HTTP contract deterministic and
        // turns policy violations into a controlled 422 before layout begins.
        EnsureAllowedResourceUrls(sanitizedHtml, resourceRetriever, cancellationToken);

        var converterProperties = BuildConverterProperties(request, resourceRetriever);
        converterPropertiesStopwatch.Stop();

        var renderStopwatch = Stopwatch.StartNew();
        var (pdfBytes, byteExtractionElapsed) = RenderPdfBytes(
            sanitizedHtml,
            converterProperties,
            cancellationToken);
        renderStopwatch.Stop();

        return new PdfRenderResult(
            pdfBytes,
            converterPropertiesStopwatch.Elapsed,
            renderStopwatch.Elapsed,
            byteExtractionElapsed);
    }

    private RestrictedResourceRetriever CreateResourceRetriever(
        ValidatedPdfRenderRequest request,
        CancellationToken cancellationToken)
    {
        return new RestrictedResourceRetriever(
            _httpClientFactory,
            _options,
            request,
            cancellationToken);
    }

    private static void EnsureAllowedResourceUrls(
        string sanitizedHtml,
        RestrictedResourceRetriever resourceRetriever,
        CancellationToken cancellationToken)
    {
        var document = new HtmlParser().ParseDocument(sanitizedHtml);

        foreach (var image in document.QuerySelectorAll("img[src]"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = image.GetAttribute("src");

            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var resolvedUrl = resourceRetriever.EnsureResourceUrlAllowed(source);

            if (string.Equals(resolvedUrl.Scheme, "data", StringComparison.OrdinalIgnoreCase))
            {
                _ = resourceRetriever.GetByteArrayByUrl(resolvedUrl);
            }
        }
    }

    private ConverterProperties BuildConverterProperties(
        ValidatedPdfRenderRequest request,
        RestrictedResourceRetriever resourceRetriever)
    {
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

    private static (byte[] Content, TimeSpan ByteExtractionElapsed) RenderPdfBytes(
        string sanitizedHtml,
        ConverterProperties converterProperties,
        CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();

        // iText/pdfHTML exposes a synchronous conversion API, so we keep the blocking
        // section narrowed to the converter call plus deterministic disposal.
        using (var writer = new PdfWriter(output))
        using (var pdfDocument = new PdfDocument(writer))
        {
            HtmlConverter.ConvertToPdf(sanitizedHtml, pdfDocument, converterProperties);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Forcing GC.Collect() per request would increase latency and reduce throughput.
        // Copying the finalized PDF bytes here avoids retaining the MemoryStream beyond the request.
        var byteExtractionStopwatch = Stopwatch.StartNew();
        var pdfBytes = output.ToArray();
        byteExtractionStopwatch.Stop();

        return (pdfBytes, byteExtractionStopwatch.Elapsed);
    }
}
