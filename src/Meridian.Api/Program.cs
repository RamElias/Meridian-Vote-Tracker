using Meridian.Infrastructure.Configuration;
using Meridian.Api.Middleware;
using Meridian.Infrastructure;
using Meridian.Api.Authentication;
using Microsoft.AspNetCore.Authentication;
using Meridian.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Configuration.AddKeyVaultIfConfigured(builder.Configuration);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = 150 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 150 * 1024 * 1024);
builder.Services.AddMeridianInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
    if (context.ProblemDetails.Status >= 500)
    {
        context.ProblemDetails.Title = "The request could not be completed.";
        context.ProblemDetails.Detail = "An internal error occurred. Use the correlation ID when reporting this problem.";
    }
});
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]) || !string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
    builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddControllers().ConfigureApiBehaviorOptions(options =>
    options.InvalidModelStateResponseFactory = context =>
    {
        var factory = context.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Mvc.Infrastructure.ProblemDetailsFactory>();
        var problem = factory.CreateValidationProblemDetails(context.HttpContext, context.ModelState,
            statusCode: 400, title: "Invalid request", detail: "Check the request fields and JSON format.");
        problem.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;
        return new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(problem);
    });
builder.Services.AddAuthentication(DemoAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DemoAuthenticationHandler>(DemoAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseMeridianRequestHandling();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
var seedPath = builder.Configuration["Seed:MemberStatesPath"];
await app.Services.GetRequiredService<DatabaseBootstrapper>().InitializeAsync(seedPath, app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program { }
