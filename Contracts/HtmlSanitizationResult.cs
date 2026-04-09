namespace PoCPdfSharp.Contracts;

public sealed record HtmlSanitizationResult(
    string Html,
    int OriginalHtmlLength,
    int SanitizedHtmlLength,
    int RemovedTagCount,
    int RemovedAttributeCount,
    int RemovedStyleCount,
    int RemovedAtRuleCount,
    int RemovedCommentCount,
    bool WasAggressive,
    bool HasUsableContent,
    IReadOnlyCollection<string> RemovedRelevantTags);
