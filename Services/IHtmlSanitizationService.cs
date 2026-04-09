using PoCPdfSharp.Contracts;

namespace PoCPdfSharp.Services;

public interface IHtmlSanitizationService
{
    HtmlSanitizationResult Sanitize(ValidatedPdfRenderRequest request, CancellationToken cancellationToken);
}
