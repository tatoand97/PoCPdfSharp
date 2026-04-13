using PoCPdfSharp.Contracts;

namespace PoCPdfSharp.Services;

public interface IRemoteImageInliningService
{
    Task<RemoteImageInliningResult> InlineAsync(
        ValidatedPdfRenderRequest request,
        string sanitizedHtml,
        CancellationToken cancellationToken);
}
