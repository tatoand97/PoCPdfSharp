namespace PoCPdfSharp.Contracts;

public sealed class PdfRenderRequest
{
    public string? Html { get; init; }

    public string? FileName { get; init; }

    public string? BaseUri { get; init; }
}
