using System.Text;
using PoCPdfSharp.Contracts;
using PoCPdfSharp.Infrastructure;

namespace PoCPdfSharp.Services;

public sealed class PdfRenderRequestValidator
{
    public ValidatedPdfRenderRequest Validate(PdfRenderRequest? request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request is null)
        {
            throw new RequestValidationException("The request body is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Html))
        {
            throw new RequestValidationException("The 'html' property is required.");
        }

        var normalizedFileName = NormalizeFileName(request.FileName);

        Uri? baseUri = null;
        string? baseUriHost = null;

        if (!string.IsNullOrWhiteSpace(request.BaseUri))
        {
            if (!Uri.TryCreate(request.BaseUri, UriKind.Absolute, out var parsedBaseUri) ||
                !string.Equals(parsedBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new RequestValidationException("The 'baseUri' property must be an absolute HTTPS URL.");
            }

            baseUri = parsedBaseUri;
            baseUriHost = parsedBaseUri.IdnHost;
        }

        return new ValidatedPdfRenderRequest(
            request.Html,
            normalizedFileName,
            request.Html.Length,
            baseUri,
            baseUriHost);
    }

    private static string NormalizeFileName(string? fileName)
    {
        const string defaultFileName = "document.pdf";

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return defaultFileName;
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(fileName.Length);

        foreach (var character in fileName.Trim())
        {
            if (character is '/' or '\\' or '"' || char.IsControl(character))
            {
                continue;
            }

            if (invalidCharacters.Contains(character))
            {
                continue;
            }

            builder.Append(character);
        }

        var normalized = builder.ToString().Trim();

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return defaultFileName;
        }

        if (!normalized.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            normalized += ".pdf";
        }

        return normalized;
    }
}
