using System.Text;
using PoCPdfSharp.Contracts;
using PoCPdfSharp.Infrastructure;

namespace PoCPdfSharp.Services;

public sealed class PdfRenderRequestValidator : IPdfRenderRequestValidator
{
    private const string DefaultFileName = "document.pdf";
    private const int MaxFileNameLength = 128;

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

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

        if (!string.IsNullOrWhiteSpace(request.BaseUri))
        {
            if (!Uri.TryCreate(request.BaseUri, UriKind.Absolute, out var parsedBaseUri) ||
                !string.Equals(parsedBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                throw new RequestValidationException("The 'baseUri' property must be an absolute HTTPS URL.");
            }

            if (!string.IsNullOrWhiteSpace(parsedBaseUri.UserInfo))
            {
                throw new RequestValidationException(
                    "The 'baseUri' property cannot include user information.");
            }

            baseUri = parsedBaseUri;
        }

        return new ValidatedPdfRenderRequest(
            request.Html,
            normalizedFileName,
            request.Html.Length,
            baseUri);
    }

    private static string NormalizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return DefaultFileName;
        }

        var candidate = Path.GetFileName(fileName.Trim());

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return DefaultFileName;
        }

        var invalidCharacters = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(candidate.Length);

        foreach (var character in candidate)
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

        var normalized = CollapseWhitespace(builder.ToString()).Trim().Trim('.');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return DefaultFileName;
        }

        if (!normalized.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            normalized += ".pdf";
        }

        normalized = TrimToMaxLength(normalized);

        var baseName = Path.GetFileNameWithoutExtension(normalized).Trim().Trim('.');

        if (string.IsNullOrWhiteSpace(baseName) || ReservedFileNames.Contains(baseName))
        {
            return DefaultFileName;
        }

        return normalized;
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;

        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (previousWasWhitespace)
                {
                    continue;
                }

                builder.Append(' ');
                previousWasWhitespace = true;
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString();
    }

    private static string TrimToMaxLength(string fileName)
    {
        if (fileName.Length <= MaxFileNameLength)
        {
            return fileName;
        }

        const string pdfExtension = ".pdf";
        var maxBaseNameLength = Math.Max(1, MaxFileNameLength - pdfExtension.Length);
        var baseName = fileName[..maxBaseNameLength].TrimEnd(' ', '.');

        if (string.IsNullOrWhiteSpace(baseName))
        {
            return DefaultFileName;
        }

        return $"{baseName}{pdfExtension}";
    }
}
