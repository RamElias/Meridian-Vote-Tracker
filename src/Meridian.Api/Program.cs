using Meridian.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Meridian")
    ?? throw new InvalidOperationException("ConnectionStrings:Meridian is required.");
builder.Services.AddSingleton<DatabaseBootstrapper>(provider => new DatabaseBootstrapper(connectionString, provider.GetRequiredService<ILogger<DatabaseBootstrapper>>()));
builder.Services.AddProblemDetails();
var app = builder.Build();
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128) correlationId = Guid.NewGuid().ToString("N");
    context.TraceIdentifier = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        await next();
});
app.UseExceptionHandler();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithName("Health");
app.MapGet("/api/v1/meta", () => Results.Ok(new { name = "Meridian Vote Tracker", version = "v1" })).WithName("GetMetadata");
app.MapGet("/openapi/v1.json", async (CancellationToken ct) => Results.Text(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "openapi.json"), ct), "application/json"));
var seedPath = builder.Configuration["Seed:MemberStatesPath"];
await app.Services.GetRequiredService<DatabaseBootstrapper>().InitializeAsync(seedPath, app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program { }
