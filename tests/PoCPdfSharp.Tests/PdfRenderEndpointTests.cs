using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using PoCPdfSharp.Contracts;

namespace PoCPdfSharp.Tests;

public sealed class PdfRenderEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _httpClient;

    public PdfRenderEndpointTests(WebApplicationFactory<Program> factory)
    {
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task PostRender_WithValidHtml_ReturnsPdfBinary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var request = new PdfRenderRequest
        {
            Html = """
                   <html>
                     <body>
                       <h1>Integration Test</h1>
                       <p>PDF generado desde HTML.</p>
                     </body>
                   </html>
                   """,
            FileName = "integration-test",
            BaseUri = "https://example.com/"
        };

        using var response = await _httpClient.PostAsJsonAsync("/api/pdf/render", request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        Assert.NotEmpty(body);
        Assert.True(body.Length > 5);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(body, 0, 5));
        Assert.NotEqual((byte)'"', body[0]);
        Assert.NotEqual((byte)'{', body[0]);
    }

    [Fact]
    public async Task PostRender_WithoutHtml_ReturnsBadRequestProblemDetails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/pdf/render",
            new
            {
                fileName = "missing-html.pdf",
                baseUri = "https://example.com/"
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemDetailsAsync(response, cancellationToken);

        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("The PDF render request is invalid.", problem.GetProperty("title").GetString());
        Assert.Contains("'html' property is required", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PostRender_WhenSanitizedHtmlHasNoUsableContent_ReturnsUnprocessableEntity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/pdf/render",
            new PdfRenderRequest
            {
                Html = "<script>alert('xss')</script>",
                FileName = "sanitized-empty",
                BaseUri = "https://example.com/"
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadProblemDetailsAsync(response, cancellationToken);

        Assert.Equal(422, problem.GetProperty("status").GetInt32());
        Assert.Equal("The sanitized HTML cannot be rendered.", problem.GetProperty("title").GetString());
        Assert.Contains("safe content", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PostRender_NormalizesFileNameInContentDisposition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/pdf/render",
            new PdfRenderRequest
            {
                Html = "<html><body><p>Nombre normalizado.</p></body></html>",
                FileName = "..\\reports\\quarterly summary 2026",
                BaseUri = "https://example.com/"
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fileName = GetReturnedFileName(response);

        Assert.Equal("quarterly summary 2026.pdf", fileName);
    }

    [Fact]
    public async Task PostRender_WithInvalidBaseUri_ReturnsBadRequestProblemDetails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/pdf/render",
            new PdfRenderRequest
            {
                Html = "<html><body><p>BaseUri inválida.</p></body></html>",
                FileName = "invalid-base-uri",
                BaseUri = "http://example.com/"
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemDetailsAsync(response, cancellationToken);

        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Contains("absolute HTTPS URL", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PostRender_WithBaseUriUserInfo_ReturnsBadRequestProblemDetails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/pdf/render",
            new PdfRenderRequest
            {
                Html = "<html><body><p>BaseUri con credenciales.</p></body></html>",
                FileName = "invalid-base-uri-user-info",
                BaseUri = "https://user:secret@example.com/"
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemDetailsAsync(response, cancellationToken);

        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Contains("cannot include user information", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PostRender_WithMalformedJson_ReturnsBadRequestProblemDetails()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _httpClient.PostAsync(
            "/api/pdf/render",
            new StringContent("{\"html\":", Encoding.UTF8, "application/json"),
            cancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await ReadProblemDetailsAsync(response, cancellationToken);

        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Equal("The HTTP request is invalid.", problem.GetProperty("title").GetString());
    }

    [Fact]
    public async Task PostRender_WhenExternalResourceAuthorityIsRejected_ReturnsUnprocessableEntity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/pdf/render",
            new PdfRenderRequest
            {
                Html = "<html><body><img src=\"https://blocked.example/image.png\" alt=\"Blocked\" /></body></html>",
                FileName = "blocked-resource",
                BaseUri = "https://example.com/"
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadProblemDetailsAsync(response, cancellationToken);

        Assert.Equal(422, problem.GetProperty("status").GetInt32());
        Assert.Contains("authority", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PostRender_WhenHttpsResourceHasNoBaseUriAnchor_ReturnsUnprocessableEntity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/pdf/render",
            new PdfRenderRequest
            {
                Html = "<html><body><img src=\"https://example.com/image.png\" alt=\"Remote\" /></body></html>",
                FileName = "missing-base-uri"
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadProblemDetailsAsync(response, cancellationToken);

        Assert.Equal(422, problem.GetProperty("status").GetInt32());
        Assert.Contains("require a valid baseUri", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PostRender_WhenDataUriMediaTypeIsNotAllowed_ReturnsUnprocessableEntity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/pdf/render",
            new PdfRenderRequest
            {
                Html = "<html><body><img src=\"data:text/plain;base64,SGVsbG8=\" alt=\"Blocked\" /></body></html>",
                FileName = "blocked-data-uri",
                BaseUri = "https://example.com/"
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadProblemDetailsAsync(response, cancellationToken);

        Assert.Equal(422, problem.GetProperty("status").GetInt32());
        Assert.Contains("data URI media type", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PostRender_WhenFileNameFallsBackToDefault_UsesDocumentPdf()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/pdf/render",
            new PdfRenderRequest
            {
                Html = "<html><body><p>Nombre reservado.</p></body></html>",
                FileName = "CON",
                BaseUri = "https://example.com/"
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fileName = GetReturnedFileName(response);

        Assert.Equal("document.pdf", fileName);
    }

    [Fact]
    public async Task PostRender_WhenOnlyHeadContentRemains_ReturnsUnprocessableEntity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        using var response = await _httpClient.PostAsJsonAsync(
            "/api/pdf/render",
            new PdfRenderRequest
            {
                Html = "<html><head><title>Solo head</title></head></html>",
                FileName = "head-only",
                BaseUri = "https://example.com/"
            },
            cancellationToken);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var problem = await ReadProblemDetailsAsync(response, cancellationToken);

        Assert.Equal(422, problem.GetProperty("status").GetInt32());
        Assert.Contains("safe content", problem.GetProperty("detail").GetString());
    }

    private static string GetReturnedFileName(HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;

        Assert.NotNull(contentDisposition);

        return contentDisposition.FileNameStar ??
               contentDisposition.FileName?.Trim('"') ??
               throw new InvalidOperationException("The response did not include a downloadable file name.");
    }

    private static async Task<JsonElement> ReadProblemDetailsAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var document = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Expected a problem details payload.");

        using (document)
        {
            var problem = document.RootElement.Clone();

            Assert.True(problem.TryGetProperty("traceId", out var traceId));
            Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));

            return problem;
        }
    }
}
