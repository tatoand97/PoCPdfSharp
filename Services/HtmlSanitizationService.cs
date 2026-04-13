using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using PoCPdfSharp.Contracts;

namespace PoCPdfSharp.Services;

public sealed partial class HtmlSanitizationService : IHtmlSanitizationService
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    private static readonly HashSet<string> AllowedTags = new(
    [
        "html", "head", "body", "title", "style",
        "main", "header", "footer", "section", "article", "figure", "figcaption",
        "div", "span", "p", "br", "hr",
        "strong", "b", "em", "i", "u", "s", "sub", "sup", "small",
        "blockquote", "code", "pre",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "ul", "ol", "li",
        "dl", "dt", "dd",
        "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
        "img", "a"
    ], Comparer);

    private static readonly HashSet<string> AllowedAttributes = new(
    [
        "class", "id", "style", "lang", "dir", "title",
        "src", "srcset", "alt", "width", "height",
        "colspan", "rowspan", "scope",
        "align", "valign", "cellpadding", "cellspacing", "border"
    ], Comparer);

    private static readonly HashSet<string> UriAttributes = new(["src"], Comparer);

    private static readonly HashSet<string> AllowedSchemes = new(["https", "data"], Comparer);

    private static readonly HashSet<string> AllowedCssProperties = new(
    [
        "color",
        "background-color",
        "background",
        "background-image",
        "font", "font-family", "font-size", "font-style", "font-weight",
        "line-height", "letter-spacing",
        "text-align", "text-decoration", "text-indent", "text-transform",
        "vertical-align", "white-space", "word-break", "word-spacing",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "border", "border-top", "border-right", "border-bottom", "border-left",
        "border-collapse", "border-color", "border-spacing", "border-style", "border-width",
        "width", "height", "min-width", "max-width", "min-height", "max-height",
        "display",
        "list-style-type",
        "table-layout",
        "page-break-before", "page-break-after", "page-break-inside",
        "break-before", "break-after", "break-inside"
    ], Comparer);

    private static readonly HashSet<string> RelevantRemovedTags = new(
    [
        "script", "iframe", "form", "input", "button", "select", "textarea",
        "object", "embed", "audio", "video", "canvas", "svg", "link", "meta"
    ], Comparer);

    private static readonly Regex DisallowedCssValuePattern = DangerousCssValueRegex();

    private readonly HtmlParser _htmlParser = new();

    public HtmlSanitizationResult Sanitize(ValidatedPdfRenderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var removedRelevantTags = new HashSet<string>(Comparer);
        var removedTagCount = 0;
        var removedAttributeCount = 0;
        var removedStyleCount = 0;
        var removedAtRuleCount = 0;
        var removedCommentCount = 0;

        var sanitizer = CreateSanitizer();
        sanitizer.RemovingTag += (_, args) =>
        {
            removedTagCount++;
            var tagName = args.Tag.LocalName;

            if (RelevantRemovedTags.Contains(tagName))
            {
                removedRelevantTags.Add(tagName);
            }
        };
        sanitizer.RemovingAttribute += (_, _) => removedAttributeCount++;
        sanitizer.RemovingStyle += (_, _) => removedStyleCount++;
        sanitizer.RemovingAtRule += (_, _) => removedAtRuleCount++;
        sanitizer.RemovingComment += (_, _) => removedCommentCount++;

        var sanitizedHtml = request.BaseUri is null
            ? sanitizer.Sanitize(request.Html)
            : sanitizer.Sanitize(request.Html, request.BaseUri.AbsoluteUri);

        cancellationToken.ThrowIfCancellationRequested();

        var sanitizedLength = sanitizedHtml.Length;
        var hasUsableContent = HasUsableContent(sanitizedHtml);
        var wasAggressive =
            removedRelevantTags.Count > 0 ||
            (request.OriginalHtmlLength > 0 &&
             sanitizedLength < (request.OriginalHtmlLength * 0.75));

        return new HtmlSanitizationResult(
            sanitizedHtml,
            request.OriginalHtmlLength,
            sanitizedLength,
            removedTagCount,
            removedAttributeCount,
            removedStyleCount,
            removedAtRuleCount,
            removedCommentCount,
            wasAggressive,
            hasUsableContent,
            removedRelevantTags.OrderBy(tag => tag, Comparer).ToArray());
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var sanitizer = new HtmlSanitizer
        {
            AllowCssCustomProperties = false,
            AllowDataAttributes = false,
            DisallowCssPropertyValue = DisallowedCssValuePattern,
            KeepChildNodes = false
        };

        ReplaceValues(sanitizer.AllowedTags, AllowedTags);
        ReplaceValues(sanitizer.AllowedAttributes, AllowedAttributes);
        ReplaceValues(sanitizer.AllowedCssProperties, AllowedCssProperties);
        sanitizer.AllowedAtRules.Clear();
        ReplaceValues(sanitizer.AllowedSchemes, AllowedSchemes);
        ReplaceValues(sanitizer.UriAttributes, UriAttributes);

        return sanitizer;
    }

    private bool HasUsableContent(string sanitizedHtml)
    {
        if (string.IsNullOrWhiteSpace(sanitizedHtml))
        {
            return false;
        }

        var document = _htmlParser.ParseDocument(sanitizedHtml);
        var body = document.Body;

        if (body is null)
        {
            return false;
        }

        var visibleText = body.TextContent;
        var hasVisibleText = !string.IsNullOrWhiteSpace(visibleText);
        var hasImage = body.QuerySelector("img") is not null;
        var hasStructuredTable = body
            .QuerySelectorAll("table")
            .Any(table => table.QuerySelector("th,td,caption") is not null &&
                          !string.IsNullOrWhiteSpace(table.TextContent));

        return hasVisibleText || hasImage || hasStructuredTable;
    }

    // We intentionally allow CSS url(...) values here so the next pipeline step can
    // validate and inline only safe HTTPS/data image references. Dangerous executable
    // schemes and non-image data payloads are still blocked at sanitization time.
    [GeneratedRegex(@"(?ix)(expression\s*\(|javascript:|vbscript:|file:|ftp:|blob:|data\s*:\s*image/svg\+xml|data\s*:\s*text/html)")]
    private static partial Regex DangerousCssValueRegex();

    private static void ReplaceValues(ISet<string> target, IEnumerable<string> values)
    {
        target.Clear();
        target.UnionWith(values);
    }
}
