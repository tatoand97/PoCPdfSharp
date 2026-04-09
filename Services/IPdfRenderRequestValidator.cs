using PoCPdfSharp.Contracts;

namespace PoCPdfSharp.Services;

public interface IPdfRenderRequestValidator
{
    ValidatedPdfRenderRequest Validate(PdfRenderRequest? request, CancellationToken cancellationToken);
}
