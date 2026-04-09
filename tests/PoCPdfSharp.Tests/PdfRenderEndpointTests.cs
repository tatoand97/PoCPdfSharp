using System.Net;
using System.Net.Http.Json;
using System.Text;
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
    public async Task PostRender_ReturnsPdfBinary()
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
    }
}
