using PoCPdfSharp.Contracts;

namespace PoCPdfSharp.Services;

public interface IHtmlToPdfRenderer
{
    PdfRenderResult Render(
        ValidatedPdfRenderRequest request,
        string sanitizedHtml,
        CancellationToken cancellationToken);
}
