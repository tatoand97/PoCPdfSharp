namespace PoCPdfSharp.Contracts;

public sealed record ValidatedPdfRenderRequest(
    string Html,
    string FileName,
    int OriginalHtmlLength,
    Uri? BaseUri,
    string? BaseUriHost);
