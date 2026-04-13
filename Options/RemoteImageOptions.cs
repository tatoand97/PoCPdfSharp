namespace PoCPdfSharp.Options;

public sealed class RemoteImageOptions
{
    public const string SectionName = "RemoteImages";

    public List<string> AllowedImageHosts { get; set; } = [];

    public long MaxImageBytes { get; set; } = 5 * 1024 * 1024;

    public int RequestTimeoutSeconds { get; set; } = 10;

    public int MaxRedirects { get; set; } = 3;

    public bool ReplaceFailedImagesWithTransparentPlaceholder { get; set; } = true;
}
