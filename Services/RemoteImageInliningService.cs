using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using PoCPdfSharp.Contracts;
using PoCPdfSharp.Options;

namespace PoCPdfSharp.Services;

public sealed partial class RemoteImageInliningService : IRemoteImageInliningService
{
    public const string HttpClientName = "RemoteImageInlining";

    private const string TransparentPlaceholderDataUri =
        "data:image/gif;base64,R0lGODlhAQABAPAAAAAAAP///ywAAAAAAQABAAACAUwAOw==";

    private static readonly HashSet<string> AllowedImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp"
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHostAddressResolver _hostAddressResolver;
    private readonly RemoteImageOptions _options;
    private readonly ILogger<RemoteImageInliningService> _logger;
    private readonly HtmlParser _htmlParser = new();

    public RemoteImageInliningService(
        IHttpClientFactory httpClientFactory,
        IHostAddressResolver hostAddressResolver,
        IOptions<RemoteImageOptions> options,
        ILogger<RemoteImageInliningService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _hostAddressResolver = hostAddressResolver;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<RemoteImageInliningResult> InlineAsync(
        ValidatedPdfRenderRequest request,
        string sanitizedHtml,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var document = _htmlParser.ParseDocument(sanitizedHtml);
        var processedResourceCount = 0;
        var inlinedResourceCount = 0;
        var failedResourceCount = 0;
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var image in document.QuerySelectorAll("img"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (image.HasAttribute("src"))
            {
                processedResourceCount++;

                if (await TryInlineImageSourceAsync(
                        request,
                        image,
                        "src",
                        image.GetAttribute("src"),
                        cache,
                        cancellationToken))
                {
                    inlinedResourceCount++;
                }
                else
                {
                    failedResourceCount++;
                }
            }

            if (image.HasAttribute("srcset"))
            {
                processedResourceCount++;

                if (await TryInlineSrcSetAsync(request, image, cache, cancellationToken))
                {
                    inlinedResourceCount++;
                }
                else
                {
                    failedResourceCount++;
                }
            }
        }

        foreach (var element in document.All.Where(node => node.HasAttribute("style")))
        {
            cancellationToken.ThrowIfCancellationRequested();
            processedResourceCount++;

            if (await TryInlineStyleUrlsAsync(request, element, cache, cancellationToken))
            {
                inlinedResourceCount++;
            }
            else
            {
                failedResourceCount++;
            }
        }

        return new RemoteImageInliningResult(
            document.DocumentElement?.OuterHtml ?? sanitizedHtml,
            processedResourceCount,
            inlinedResourceCount,
            failedResourceCount);
    }

    private async Task<bool> TryInlineImageSourceAsync(
        ValidatedPdfRenderRequest request,
        IElement element,
        string attributeName,
        string? rawValue,
        IDictionary<string, string> cache,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        try
        {
            var resolved = await ResolveImageDataUriAsync(request, rawValue.Trim(), cache, cancellationToken);
            element.SetAttribute(attributeName, resolved);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            HandleImageFailure(element, attributeName, rawValue, exception);
            return false;
        }
    }

    private async Task<bool> TryInlineSrcSetAsync(
        ValidatedPdfRenderRequest request,
        IElement image,
        IDictionary<string, string> cache,
        CancellationToken cancellationToken)
    {
        var srcSet = image.GetAttribute("srcset");

        if (string.IsNullOrWhiteSpace(srcSet))
        {
            return true;
        }

        try
        {
            var updated = await ReplaceSrcSetUrlsAsync(request, srcSet, cache, cancellationToken);
            image.SetAttribute("srcset", updated);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            HandleImageFailure(image, "srcset", srcSet, exception);
            image.RemoveAttribute("srcset");
            return false;
        }
    }

    private async Task<bool> TryInlineStyleUrlsAsync(
        ValidatedPdfRenderRequest request,
        IElement element,
        IDictionary<string, string> cache,
        CancellationToken cancellationToken)
    {
        var style = element.GetAttribute("style");

        if (string.IsNullOrWhiteSpace(style))
        {
            return true;
        }

        try
        {
            var updated = await ReplaceStyleUrlsAsync(request, style, cache, cancellationToken);
            element.SetAttribute("style", updated);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "Replacing inline style image references with safe placeholder. StyleSnippet={StyleSnippet}",
                style.Length > 200 ? style[..200] : style);

            element.SetAttribute("style", ReplaceStyleUrlsWithPlaceholder(style));
            return false;
        }
    }

    private async Task<string> ReplaceSrcSetUrlsAsync(
        ValidatedPdfRenderRequest request,
        string srcSet,
        IDictionary<string, string> cache,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(srcSet.Length);
        var index = 0;

        while (index < srcSet.Length)
        {
            while (index < srcSet.Length && char.IsWhiteSpace(srcSet[index]))
            {
                builder.Append(srcSet[index]);
                index++;
            }

            if (index >= srcSet.Length)
            {
                break;
            }

            var urlStart = index;
            var isDataUri = srcSet.AsSpan(index).StartsWith("data:", StringComparison.OrdinalIgnoreCase);

            while (index < srcSet.Length)
            {
                var current = srcSet[index];

                if (char.IsWhiteSpace(current) || (!isDataUri && current == ','))
                {
                    break;
                }

                index++;
            }

            var rawUrl = srcSet[urlStart..index];
            var replacement = await ResolveImageDataUriAsync(request, rawUrl, cache, cancellationToken);
            builder.Append(replacement);

            while (index < srcSet.Length && srcSet[index] != ',')
            {
                builder.Append(srcSet[index]);
                index++;
            }

            if (index < srcSet.Length && srcSet[index] == ',')
            {
                builder.Append(',');
                index++;
            }
        }

        return builder.ToString();
    }

    private async Task<string> ReplaceStyleUrlsAsync(
        ValidatedPdfRenderRequest request,
        string style,
        IDictionary<string, string> cache,
        CancellationToken cancellationToken)
    {
        var matches = CssUrlRegex().Matches(style);

        if (matches.Count == 0)
        {
            return style;
        }

        var builder = new StringBuilder(style.Length + (matches.Count * 64));
        var previousIndex = 0;

        foreach (Match match in matches)
        {
            builder.Append(style, previousIndex, match.Index - previousIndex);
            var rawUrl = match.Groups["value"].Value.Trim();
            var quote = match.Groups["quote"].Success ? match.Groups["quote"].Value : "'";
            var replacement = await ResolveImageDataUriAsync(request, rawUrl, cache, cancellationToken);
            builder.Append("url(");
            builder.Append(quote);
            builder.Append(replacement);
            builder.Append(quote);
            builder.Append(')');
            previousIndex = match.Index + match.Length;
        }

        builder.Append(style, previousIndex, style.Length - previousIndex);
        return builder.ToString();
    }

    private string ReplaceStyleUrlsWithPlaceholder(string style)
    {
        return CssUrlRegex().Replace(style, _ => $"url('{TransparentPlaceholderDataUri}')");
    }

    private async Task<string> ResolveImageDataUriAsync(
        ValidatedPdfRenderRequest request,
        string rawValue,
        IDictionary<string, string> cache,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(rawValue, out var cached))
        {
            return cached;
        }

        var result = await ResolveImageDataUriCoreAsync(request, rawValue, cancellationToken);
        cache[rawValue] = result;
        return result;
    }

    private async Task<string> ResolveImageDataUriCoreAsync(
        ValidatedPdfRenderRequest request,
        string rawValue,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return TransparentPlaceholderDataUri;
        }

        if (rawValue.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            ValidateDataImageUri(rawValue);
            return rawValue;
        }

        var imageUri = ResolveAndValidateCandidateUri(request, rawValue);
        await EnsurePublicDestinationAsync(imageUri, cancellationToken);

        var (bytes, mediaType) = await DownloadImageAsync(imageUri, cancellationToken);
        return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    private Uri ResolveAndValidateCandidateUri(ValidatedPdfRenderRequest request, string rawValue)
    {
        if (!Uri.TryCreate(rawValue, UriKind.RelativeOrAbsolute, out var candidate))
        {
            throw new InvalidOperationException($"Image URL '{rawValue}' is malformed.");
        }

        if (!candidate.IsAbsoluteUri)
        {
            if (request.BaseUri is null)
            {
                throw new InvalidOperationException(
                    $"Relative image URL '{rawValue}' requires a valid baseUri.");
            }

            candidate = new Uri(request.BaseUri, candidate);
        }

        return ValidateAbsoluteHttpsUri(candidate);
    }

    private Uri ValidateAbsoluteHttpsUri(Uri candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.UserInfo))
        {
            throw new InvalidOperationException("Image URLs cannot include user information.");
        }

        if (!string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Only HTTPS image URLs are allowed. Rejected scheme '{candidate.Scheme}'.");
        }

        if (!_options.AllowedImageHosts.Contains(candidate.IdnHost, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Image host '{candidate.IdnHost}' is not present in the allowlist.");
        }

        if (IsBlockedHostName(candidate.IdnHost))
        {
            throw new InvalidOperationException(
                $"Image host '{candidate.IdnHost}' is not allowed for remote fetching.");
        }

        return candidate;
    }

    private async Task EnsurePublicDestinationAsync(Uri imageUri, CancellationToken cancellationToken)
    {
        var host = imageUri.IdnHost;

        if (IPAddress.TryParse(host, out var literalAddress))
        {
            EnsureAddressAllowed(imageUri, literalAddress);
            return;
        }

        var resolvedAddresses = await _hostAddressResolver.ResolveAsync(host, cancellationToken);

        if (resolvedAddresses.Count == 0)
        {
            throw new InvalidOperationException($"Image host '{host}' did not resolve to any address.");
        }

        foreach (var address in resolvedAddresses)
        {
            EnsureAddressAllowed(imageUri, address);
        }
    }

    private void EnsureAddressAllowed(Uri imageUri, IPAddress address)
    {
        // This blocks loopback, RFC1918 private ranges, link-local ranges, metadata endpoints,
        // and other non-public addresses so a public-looking host cannot pivot into internal SSRF.
        if (IsBlockedAddress(address))
        {
            throw new InvalidOperationException(
                $"Image destination '{imageUri.Host}' resolved to blocked address '{address}'.");
        }
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadImageAsync(Uri imageUri, CancellationToken cancellationToken)
    {
        using var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutTokenSource.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));

        try
        {
            var currentUri = imageUri;

            for (var redirectCount = 0; redirectCount <= _options.MaxRedirects; redirectCount++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                using var response = await _httpClientFactory
                    .CreateClient(HttpClientName)
                    .SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeoutTokenSource.Token);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount == _options.MaxRedirects)
                    {
                        throw new InvalidOperationException(
                            $"Image URL '{imageUri}' exceeded the redirect limit.");
                    }

                    var location = response.Headers.Location
                        ?? throw new InvalidOperationException(
                            $"Image URL '{currentUri}' returned a redirect without Location.");

                    var redirectedUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                    redirectedUri = ValidateAbsoluteHttpsUri(redirectedUri);
                    await EnsurePublicDestinationAsync(redirectedUri, timeoutTokenSource.Token);
                    currentUri = redirectedUri;
                    continue;
                }

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw new InvalidOperationException(
                        $"Image URL '{currentUri}' returned status {(int)response.StatusCode}.");
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType;

                if (string.IsNullOrWhiteSpace(mediaType) || !AllowedImageMediaTypes.Contains(mediaType))
                {
                    throw new InvalidOperationException(
                        $"Image URL '{currentUri}' returned unsupported content type '{mediaType ?? "(missing)"}'.");
                }

                var contentLength = response.Content.Headers.ContentLength;

                if (contentLength is > 0 && contentLength > _options.MaxImageBytes)
                {
                    throw new InvalidOperationException(
                        $"Image URL '{currentUri}' exceeded the {_options.MaxImageBytes} byte limit.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(timeoutTokenSource.Token);
                var bytes = await ReadAllBytesWithLimitAsync(stream, currentUri, timeoutTokenSource.Token);
                return (bytes, mediaType);
            }

            throw new InvalidOperationException($"Image URL '{imageUri}' could not be downloaded.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out while downloading image '{imageUri}'.");
        }
        catch (HttpRequestException exception)
        {
            throw new InvalidOperationException(
                $"Failed to download image '{imageUri}'. {exception.Message}",
                exception);
        }
    }

    private async Task<byte[]> ReadAllBytesWithLimitAsync(
        Stream stream,
        Uri imageUri,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);

        try
        {
            await using var output = new MemoryStream();
            long totalRead = 0;

            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

                if (read == 0)
                {
                    break;
                }

                totalRead += read;

                if (totalRead > _options.MaxImageBytes)
                {
                    throw new InvalidOperationException(
                        $"Image URL '{imageUri}' exceeded the {_options.MaxImageBytes} byte limit.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return output.ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Timed out while downloading image '{imageUri}'.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ValidateDataImageUri(string dataUri)
    {
        var commaIndex = dataUri.IndexOf(',');

        if (commaIndex <= "data:".Length)
        {
            throw new InvalidOperationException("The data URI image is malformed.");
        }

        var metadata = dataUri["data:".Length..commaIndex];

        if (!metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only base64-encoded data URI images are allowed.");
        }

        var mediaType = metadata[..^";base64".Length];

        if (!AllowedImageMediaTypes.Contains(mediaType))
        {
            throw new InvalidOperationException(
                $"The data URI image media type '{mediaType}' is not allowed.");
        }

        _ = Convert.FromBase64String(dataUri[(commaIndex + 1)..]);
    }

    private void HandleImageFailure(IElement element, string attributeName, string rawValue, Exception exception)
    {
        _logger.LogWarning(
            exception,
            "Failed to inline remote image. Attribute={AttributeName} Value={ImageValue}",
            attributeName,
            rawValue);

        if (!_options.ReplaceFailedImagesWithTransparentPlaceholder)
        {
            element.RemoveAttribute(attributeName);
            return;
        }

        if (string.Equals(attributeName, "srcset", StringComparison.OrdinalIgnoreCase))
        {
            element.RemoveAttribute(attributeName);
            return;
        }

        element.SetAttribute(attributeName, TransparentPlaceholderDataUri);
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        var numeric = (int)statusCode;
        return numeric is >= 300 and < 400;
    }

    private static bool IsBlockedHostName(string host)
    {
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC;
        }

        var octets = address.GetAddressBytes();

        if (octets.Length != 4)
        {
            return true;
        }

        if (octets[0] == 10 ||
            octets[0] == 127 ||
            (octets[0] == 169 && octets[1] == 254) ||
            (octets[0] == 172 && octets[1] is >= 16 and <= 31) ||
            (octets[0] == 192 && octets[1] == 168) ||
            (octets[0] == 100 && octets[1] is >= 64 and <= 127) ||
            (octets[0] == 198 && octets[1] is 18 or 19))
        {
            return true;
        }

        return address.Equals(IPAddress.Parse("169.254.169.254")) ||
               address.Equals(IPAddress.Parse("100.100.100.200"));
    }

    [GeneratedRegex(@"url\(\s*(?<quote>['""]?)(?<value>.*?)\k<quote>\s*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CssUrlRegex();
}
