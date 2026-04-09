namespace PoCPdfSharp.Options;

public sealed class PdfRenderingOptions
{
    public const string SectionName = "PdfRendering";

    public long MaxResourceBytes { get; set; } = 2 * 1024 * 1024;

    public int ResourceTimeoutSeconds { get; set; } = 10;

    public bool AllowHttpsResources { get; set; } = true;

    public bool AllowDataUriImages { get; set; } = true;

    public bool RestrictToBaseUriHost { get; set; } = true;

    public int MaxLayoutPasses { get; set; } = 4096;
}
