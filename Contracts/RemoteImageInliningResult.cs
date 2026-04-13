namespace PoCPdfSharp.Contracts;

public sealed record RemoteImageInliningResult(
    string Html,
    int ProcessedResourceCount,
    int InlinedResourceCount,
    int FailedResourceCount);
