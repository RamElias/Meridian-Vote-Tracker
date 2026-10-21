using Meridian.Infrastructure;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 150 * 1024 * 1024);
var connectionString = builder.Configuration.GetConnectionString("Meridian")
    ?? throw new InvalidOperationException("ConnectionStrings:Meridian is required.");
builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<CsvImportValidator>();
builder.Services.AddSingleton<ImportService>(provider => new ImportService(provider.GetRequiredService<NpgsqlDataSource>(), builder.Configuration["Storage:LocalPath"]));
builder.Services.AddSingleton<DatabaseBootstrapper>(provider => new DatabaseBootstrapper(connectionString, provider.GetRequiredService<ILogger<DatabaseBootstrapper>>()));
builder.Services.AddProblemDetails();
var app = builder.Build();
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128 || correlationId.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '_' or '-'))) correlationId = Guid.NewGuid().ToString("N");
    context.TraceIdentifier = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    using (app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        await next();
});
app.UseExceptionHandler();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithName("Health");
app.MapGet("/api/v1/meta", () => Results.Ok(new { name = "Meridian Vote Tracker", version = "v1" })).WithName("GetMetadata");
app.MapGet("/openapi/v1.json", async (CancellationToken ct) => Results.Text(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "openapi.json"), ct), "application/json"));
app.MapPost("/api/v1/imports", async (HttpRequest request, ImportService imports, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.Problem(title: "Invalid upload", detail: "Expected multipart/form-data with a CSV file in the 'file' field.", statusCode: StatusCodes.Status400BadRequest);
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.Problem(title: "Invalid upload", detail: "The multipart form must contain a non-empty 'file' CSV.", statusCode: StatusCodes.Status400BadRequest);
    try
    {
        await using var stream = file.OpenReadStream();
        var result = await imports.CreateAsync(file.FileName, request.HttpContext.TraceIdentifier, stream, ct);
        var location = $"/api/v1/imports/{result.ImportId}";
        return result.Status == "Duplicate" ? Results.Ok(result) : Results.Accepted(location, result);
    }
    catch (InvalidDataException exception)
    {
        return Results.Problem(title: "Invalid upload", detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
    }
}).WithName("CreateImport");
app.MapGet("/api/v1/imports", async (ImportService imports, CancellationToken ct) => Results.Ok(await imports.ListAsync(ct))).WithName("ListImports");
app.MapGet("/api/v1/imports/{id:guid}/rows", async (Guid id, long? after, int? limit, string? classification, ImportService imports, CancellationToken ct) =>
{
    var pageSize = Math.Clamp(limit ?? 200, 1, 500);
    var rows = await imports.ListRowsAsync(id, Math.Max(0, after ?? 0), pageSize, classification, ct);
    return Results.Ok(rows);
}).WithName("ListImportRows");app.MapGet("/api/v1/imports/{id:guid}", async (Guid id, ImportService imports, CancellationToken ct) =>
{
    var result = await imports.GetAsync(id, ct);
    return result is null ? Results.Problem(title: "Import not found", statusCode: StatusCodes.Status404NotFound) : Results.Ok(result);
}).WithName("GetImport");
var seedPath = builder.Configuration["Seed:MemberStatesPath"];
await app.Services.GetRequiredService<DatabaseBootstrapper>().InitializeAsync(seedPath, app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program { }





