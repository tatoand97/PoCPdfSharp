using System.Diagnostics;
using System.Net;
using PoCPdfSharp.Endpoints;
using PoCPdfSharp.Infrastructure;
using PoCPdfSharp.Options;
using PoCPdfSharp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.Configure(options =>
{
    options.ActivityTrackingOptions =
        ActivityTrackingOptions.TraceId |
        ActivityTrackingOptions.SpanId |
        ActivityTrackingOptions.ParentId;
});

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] =
            Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services
    .AddOptions<PdfRenderingOptions>()
    .Bind(builder.Configuration.GetSection(PdfRenderingOptions.SectionName))
    .Validate(options => options.MaxResourceBytes > 0,
        "PdfRendering:MaxResourceBytes must be greater than zero.")
    .Validate(options => options.ResourceTimeoutSeconds > 0,
        "PdfRendering:ResourceTimeoutSeconds must be greater than zero.")
    .Validate(options => options.MaxLayoutPasses > 0,
        "PdfRendering:MaxLayoutPasses must be greater than zero.")
    .ValidateOnStart();

builder.Services.AddScoped<IPdfRenderRequestValidator, PdfRenderRequestValidator>();
builder.Services.AddScoped<IHtmlSanitizationService, HtmlSanitizationService>();
builder.Services.AddScoped<IHtmlToPdfRenderer, HtmlToPdfRenderer>();

builder.Services.AddHttpClient(RestrictedResourceRetriever.HttpClientName, client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PoCPdfSharp/1.0");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = DecompressionMethods.Deflate | DecompressionMethods.GZip
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPdfRenderEndpoints();

app.Run();

public partial class Program;
