namespace PoCPdfSharp.Contracts;

public sealed record PdfRenderResult(
    byte[] Content,
    TimeSpan ConverterPropertiesElapsed,
    TimeSpan RenderElapsed,
    TimeSpan ByteExtractionElapsed);
