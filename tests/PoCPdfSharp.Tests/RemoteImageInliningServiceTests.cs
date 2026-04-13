using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PoCPdfSharp.Contracts;
using PoCPdfSharp.Options;
using PoCPdfSharp.Services;

namespace PoCPdfSharp.Tests;

public sealed class RemoteImageInliningServiceTests
{
    private static readonly IPAddress PublicTestIp = IPAddress.Parse("93.184.216.34");

    [Fact]
    public async Task InlineAsync_WithAllowedHost_ReplacesRemoteUrlWithBase64()
    {
        var html = "<html><body><img src=\"https://cdn.example.com/image.png\" /></body></html>";
        var service = CreateService(
            allowedHosts: ["cdn.example.com"],
            hostResolver: new StubHostAddressResolver(("cdn.example.com", [PublicTestIp])),
            handler: new StubHttpMessageHandler(_ => CreateResponse(HttpStatusCode.OK, "image/png", [1, 2, 3, 4])));

        var result = await service.InlineAsync(CreateRequest(html), html, TestContext.Current.CancellationToken);

        Assert.Contains("data:image/png;base64,AQIDBA==", result.Html);
        Assert.Equal(1, result.ProcessedResourceCount);
        Assert.Equal(1, result.InlinedResourceCount);
        Assert.Equal(0, result.FailedResourceCount);
    }

    [Fact]
    public async Task InlineAsync_WithHostNotAllowed_ReplacesImageWithPlaceholder()
    {
        var html = "<html><body><img src=\"https://blocked.example/image.png\" /></body></html>";
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Should not be called."));
        var service = CreateService(
            allowedHosts: ["cdn.example.com"],
            hostResolver: new StubHostAddressResolver(),
            handler: handler);

        var result = await service.InlineAsync(CreateRequest(html), html, TestContext.Current.CancellationToken);

        Assert.Contains("data:image/gif;base64", result.Html);
        Assert.DoesNotContain("https://blocked.example/image.png", result.Html);
        Assert.Equal(1, result.FailedResourceCount);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task InlineAsync_WhenRedirectTargetsHostOutsideAllowlist_ReplacesImageWithPlaceholder()
    {
        var html = "<html><body><img src=\"https://cdn.example.com/image.png\" /></body></html>";
        var service = CreateService(
            allowedHosts: ["cdn.example.com"],
            hostResolver: new StubHostAddressResolver(("cdn.example.com", [PublicTestIp])),
            handler: new StubHttpMessageHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri("https://blocked.example/redirected.png");
                return response;
            }));

        var result = await service.InlineAsync(CreateRequest(html), html, TestContext.Current.CancellationToken);

        Assert.Contains("data:image/gif;base64", result.Html);
        Assert.Equal(1, result.FailedResourceCount);
    }

    [Fact]
    public async Task InlineAsync_WhenContentTypeIsInvalid_ReplacesImageWithPlaceholder()
    {
        var html = "<html><body><img src=\"https://cdn.example.com/image.txt\" /></body></html>";
        var service = CreateService(
            allowedHosts: ["cdn.example.com"],
            hostResolver: new StubHostAddressResolver(("cdn.example.com", [PublicTestIp])),
            handler: new StubHttpMessageHandler(_ => CreateResponse(HttpStatusCode.OK, "text/plain", [1, 2, 3])));

        var result = await service.InlineAsync(CreateRequest(html), html, TestContext.Current.CancellationToken);

        Assert.Contains("data:image/gif;base64", result.Html);
        Assert.Equal(1, result.FailedResourceCount);
    }

    [Fact]
    public async Task InlineAsync_WhenImageIsTooLarge_ReplacesImageWithPlaceholder()
    {
        var html = "<html><body><img src=\"https://cdn.example.com/big.png\" /></body></html>";
        var service = CreateService(
            allowedHosts: ["cdn.example.com"],
            maxImageBytes: 4,
            hostResolver: new StubHostAddressResolver(("cdn.example.com", [PublicTestIp])),
            handler: new StubHttpMessageHandler(_ => CreateResponse(HttpStatusCode.OK, "image/png", [1, 2, 3, 4, 5])));

        var result = await service.InlineAsync(CreateRequest(html), html, TestContext.Current.CancellationToken);

        Assert.Contains("data:image/gif;base64", result.Html);
        Assert.Equal(1, result.FailedResourceCount);
    }

    [Fact]
    public async Task InlineAsync_WhenDownloadTimesOut_ReplacesImageWithPlaceholder()
    {
        var html = "<html><body><img src=\"https://cdn.example.com/slow.png\" /></body></html>";
        var service = CreateService(
            allowedHosts: ["cdn.example.com"],
            requestTimeoutSeconds: 1,
            hostResolver: new StubHostAddressResolver(("cdn.example.com", [PublicTestIp])),
            handler: new StubHttpMessageHandler(_ => throw new TaskCanceledException("Simulated timeout.")));

        var result = await service.InlineAsync(CreateRequest(html), html, TestContext.Current.CancellationToken);

        Assert.Contains("data:image/gif;base64", result.Html);
        Assert.Equal(1, result.FailedResourceCount);
    }

    [Fact]
    public async Task InlineAsync_ReplacesStyleAndSrcSetUrlsWithBase64()
    {
        var html = """
                   <html>
                     <body>
                       <img srcset="https://cdn.example.com/one.png 1x, https://cdn.example.com/two.png 2x" />
                       <div style="background-image:url('https://cdn.example.com/bg.png')"></div>
                     </body>
                   </html>
                   """;

        var service = CreateService(
            allowedHosts: ["cdn.example.com"],
            hostResolver: new StubHostAddressResolver(("cdn.example.com", [PublicTestIp])),
            handler: new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/one.png" => CreateResponse(HttpStatusCode.OK, "image/png", [1]),
                "/two.png" => CreateResponse(HttpStatusCode.OK, "image/png", [2]),
                "/bg.png" => CreateResponse(HttpStatusCode.OK, "image/png", [3]),
                _ => throw new InvalidOperationException("Unexpected URL.")
            }));

        var result = await service.InlineAsync(CreateRequest(html), html, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("https://cdn.example.com", result.Html);
        Assert.Contains("data:image/png;base64,AQ== 1x", result.Html);
        Assert.Contains("data:image/png;base64,Ag== 2x", result.Html);
        Assert.Contains("background-image:url('data:image/png;base64,Aw==')", result.Html);
        Assert.Equal(2, result.ProcessedResourceCount);
        Assert.Equal(2, result.InlinedResourceCount);
        Assert.Equal(0, result.FailedResourceCount);
    }

    private static ValidatedPdfRenderRequest CreateRequest(string html)
    {
        return new ValidatedPdfRenderRequest(
            html,
            "document.pdf",
            html.Length,
            new Uri("https://app.example.com/"));
    }

    private static RemoteImageInliningService CreateService(
        IReadOnlyCollection<string> allowedHosts,
        IHostAddressResolver hostResolver,
        HttpMessageHandler handler,
        long maxImageBytes = 5 * 1024 * 1024,
        int requestTimeoutSeconds = 10)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(RemoteImageInliningService.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();

        return new RemoteImageInliningService(
            factory,
            hostResolver,
            Options.Create(new RemoteImageOptions
            {
                AllowedImageHosts = [.. allowedHosts],
                MaxImageBytes = maxImageBytes,
                RequestTimeoutSeconds = requestTimeoutSeconds,
                MaxRedirects = 1,
                ReplaceFailedImagesWithTransparentPlaceholder = true
            }),
            NullLogger<RemoteImageInliningService>.Instance);
    }

    private static HttpResponseMessage CreateResponse(
        HttpStatusCode statusCode,
        string contentType,
        byte[] content)
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        body.Headers.ContentLength = content.Length;

        return new HttpResponseMessage(statusCode)
        {
            Content = body
        };
    }

    private sealed class StubHostAddressResolver(params (string Host, IReadOnlyCollection<IPAddress> Addresses)[] entries)
        : IHostAddressResolver
    {
        private readonly Dictionary<string, IReadOnlyCollection<IPAddress>> _entries =
            entries.ToDictionary(entry => entry.Host, entry => entry.Addresses, StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyCollection<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            if (_entries.TryGetValue(host, out var addresses))
            {
                return Task.FromResult(addresses);
            }

            return Task.FromResult<IReadOnlyCollection<IPAddress>>([]);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_responder(request));
        }
    }
}
