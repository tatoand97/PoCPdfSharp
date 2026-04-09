using System.Buffers;
using iText.StyledXmlParser.Resolver.Resource;
using Microsoft.Extensions.Options;
using PoCPdfSharp.Contracts;
using PoCPdfSharp.Options;

namespace PoCPdfSharp.Infrastructure;

#pragma warning disable CS0618
// pdfHTML 6.3.2 still expects the StyledXmlParser retriever interface here.
public sealed class RestrictedResourceRetriever : IResourceRetriever
#pragma warning restore CS0618
{
    public const string HttpClientName = "PdfRenderResources";

    private static readonly HashSet<string> AllowedImageMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp"
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<RestrictedResourceRetriever> _logger;
    private readonly PdfRenderingOptions _options;
    private readonly ValidatedPdfRenderRequest _request;
    private readonly CancellationToken _cancellationToken;

    public RestrictedResourceRetriever(
        IHttpClientFactory httpClientFactory,
        IOptions<PdfRenderingOptions> options,
        ILogger<RestrictedResourceRetriever> logger,
        ValidatedPdfRenderRequest request,
        CancellationToken cancellationToken)
    {
        _httpClient = httpClientFactory.CreateClient(HttpClientName);
        _logger = logger;
        _options = options.Value;
        _request = request;
        _cancellationToken = cancellationToken;
    }

    public Stream GetInputStreamByUrl(Uri url)
    {
        var bytes = GetByteArrayByUrl(url);
        return new MemoryStream(bytes, writable: false);
    }

    public byte[] GetByteArrayByUrl(Uri url)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        var resolvedUrl = ResolveUrl(url);

        if (string.Equals(resolvedUrl.Scheme, "data", StringComparison.OrdinalIgnoreCase))
        {
            return ReadDataUriImage(resolvedUrl);
        }

        return ReadHttpsImage(resolvedUrl);
    }

    public Uri EnsureResourceUrlAllowed(string resourceUrl)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(resourceUrl))
        {
            throw Reject("The resource URL is empty.");
        }

        if (!Uri.TryCreate(resourceUrl, UriKind.RelativeOrAbsolute, out var url))
        {
            throw Reject($"The resource URL '{resourceUrl}' is malformed.");
        }

        return ResolveUrl(url);
    }

    private Uri ResolveUrl(Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            if (_request.BaseUri is null)
            {
                throw Reject($"Relative resource URLs are not allowed without a valid baseUri. Resource: '{url}'.");
            }

            url = new Uri(_request.BaseUri, url);
        }

        if (!string.IsNullOrWhiteSpace(url.UserInfo))
        {
            throw Reject("Resource URLs cannot include user information.");
        }

        if (string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            if (!_options.AllowHttpsResources)
            {
                throw Reject($"HTTPS resources are disabled. Resource: '{url}'.");
            }

            EnforceBaseUriAuthority(url);
            return url;
        }

        if (string.Equals(url.Scheme, "data", StringComparison.OrdinalIgnoreCase))
        {
            if (!_options.AllowDataUriImages)
            {
                throw Reject("Data URI images are disabled.");
            }

            return url;
        }

        throw Reject($"The resource URL scheme '{url.Scheme}' is not allowed.");
    }

    private void EnforceBaseUriAuthority(Uri resolvedUrl)
    {
        if (!_options.RestrictToBaseUriHost || _request.BaseUri is null)
        {
            return;
        }

        if (!string.Equals(resolvedUrl.IdnHost, _request.BaseUri.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            resolvedUrl.Port != _request.BaseUri.Port)
        {
            throw Reject(
                $"The resource authority '{resolvedUrl.Authority}' is not allowed. Expected authority '{_request.BaseUri.Authority}'.");
        }
    }

    private byte[] ReadHttpsImage(Uri url)
    {
        using var timeoutTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken);
        timeoutTokenSource.CancelAfter(TimeSpan.FromSeconds(_options.ResourceTimeoutSeconds));

        try
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = _httpClient.Send(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutTokenSource.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw Reject($"The remote resource '{url}' returned status {(int)response.StatusCode}.");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;

            if (string.IsNullOrWhiteSpace(mediaType) || !AllowedImageMediaTypes.Contains(mediaType))
            {
                throw Reject(
                    $"The remote resource '{url}' returned unsupported media type '{mediaType ?? "(missing)"}'.");
            }

            var contentLength = response.Content.Headers.ContentLength;

            if (contentLength is > 0 && contentLength > _options.MaxResourceBytes)
            {
                throw Reject(
                    $"The remote resource '{url}' exceeded the {_options.MaxResourceBytes} byte limit.");
            }

            using var responseStream = response.Content.ReadAsStream(timeoutTokenSource.Token);

            return ReadAllBytesWithLimit(responseStream, url, timeoutTokenSource.Token);
        }
        catch (UnprocessableHtmlException)
        {
            throw;
        }
        catch (OperationCanceledException) when (!_cancellationToken.IsCancellationRequested)
        {
            throw Reject($"Timed out while retrieving remote resource '{url}'.");
        }
        catch (HttpRequestException exception)
        {
            throw Reject($"Failed to retrieve remote resource '{url}'. {exception.Message}");
        }
    }

    private byte[] ReadDataUriImage(Uri url)
    {
        var original = url.OriginalString;
        var commaIndex = original.IndexOf(',');

        if (commaIndex <= "data:".Length)
        {
            throw Reject("The data URI image is malformed.");
        }

        var metadata = original["data:".Length..commaIndex];

        if (!metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            throw Reject("Only base64-encoded data URI images are allowed.");
        }

        var mediaType = metadata[..^";base64".Length];

        if (!AllowedImageMediaTypes.Contains(mediaType))
        {
            throw Reject($"The data URI media type '{mediaType}' is not allowed.");
        }

        var payload = original[(commaIndex + 1)..].Trim();
        var estimatedLength = EstimateBase64DecodedLength(payload);

        if (estimatedLength > _options.MaxResourceBytes)
        {
            throw Reject($"The data URI image exceeded the {_options.MaxResourceBytes} byte limit.");
        }

        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(payload);
        }
        catch (FormatException exception)
        {
            throw Reject($"The data URI image is not valid base64. {exception.Message}");
        }

        if (bytes.Length > _options.MaxResourceBytes)
        {
            throw Reject($"The data URI image exceeded the {_options.MaxResourceBytes} byte limit.");
        }

        return bytes;
    }

    private byte[] ReadAllBytesWithLimit(Stream inputStream, Uri url, CancellationToken timeoutToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);

        try
        {
            using var output = new MemoryStream();
            long totalRead = 0;

            while (true)
            {
                _cancellationToken.ThrowIfCancellationRequested();

                // pdfHTML pulls resources through a synchronous interface. We keep the public
                // boundary synchronous, but use timeout-aware async reads here so the body
                // download cannot outlive the configured remote-resource timeout.
                var bytesRead = inputStream.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        timeoutToken)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

                if (bytesRead == 0)
                {
                    break;
                }

                totalRead += bytesRead;

                if (totalRead > _options.MaxResourceBytes)
                {
                    throw Reject($"The remote resource '{url}' exceeded the {_options.MaxResourceBytes} byte limit.");
                }

                output.Write(buffer, 0, bytesRead);
            }

            return output.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private UnprocessableHtmlException Reject(string message)
    {
        _logger.LogWarning(
            "Rejected external resource while rendering PDF. FileName={FileName} BaseUriHost={BaseUriHost} Reason={Reason}",
            _request.FileName,
            _request.BaseUriHost,
            message);

        return new UnprocessableHtmlException(message);
    }

    private static long EstimateBase64DecodedLength(string payload)
    {
        var sanitizedLength = payload.Count(character => !char.IsWhiteSpace(character));

        if (sanitizedLength == 0)
        {
            return 0;
        }

        var padding = payload.EndsWith("==", StringComparison.Ordinal) ? 2 :
            payload.EndsWith('=') ? 1 : 0;

        return (sanitizedLength / 4L) * 3L - padding;
    }
}
