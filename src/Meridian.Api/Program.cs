using Meridian.Infrastructure;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 150 * 1024 * 1024);
var connectionString = builder.Configuration.GetConnectionString("Meridian")
    ?? throw new InvalidOperationException("ConnectionStrings:Meridian is required.");
builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<CsvImportValidator>();
builder.Services.AddSingleton<DemoUserService>();
builder.Services.AddSingleton<ConflictService>();
builder.Services.AddSingleton<VoteService>();
builder.Services.AddSingleton<SnapshotService>();
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
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1") && !context.Request.Path.Equals("/api/v1/meta", StringComparison.OrdinalIgnoreCase))
    {
        var userId = context.Request.Headers["X-Demo-User"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(userId))
        {
            await Results.Problem(title: "Authentication required", detail: "Choose a seeded demo user in X-Demo-User.", statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
            return;
        }
        var user = await context.RequestServices.GetRequiredService<DemoUserService>().FindAsync(userId, context.RequestAborted);
        if (user is null)
        {
            await Results.Problem(title: "Unknown demo user", detail: "Use one of the seeded demo user IDs: dana, omer, or lana.", statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
            return;
        }
        context.Items["DemoUser"] = user;
    }
    await next();
});
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
        var result = await imports.CreateAsync(file.FileName, request.HttpContext.TraceIdentifier, ((DemoUser)request.HttpContext.Items["DemoUser"]!).UserId, stream, ct);
        var location = $"/api/v1/imports/{result.ImportId}";
        return result.Status == "Duplicate" ? Results.Ok(result) : Results.Accepted(location, result);
    }
    catch (InvalidDataException exception)
    {
        return Results.Problem(title: "Invalid upload", detail: exception.Message, statusCode: StatusCodes.Status400BadRequest);
    }
}).WithName("CreateImport");
app.MapGet("/api/v1/imports", async (int? offset, int? limit, ImportService imports, CancellationToken ct) => Results.Ok(await imports.ListAsync(offset ?? 0, limit ?? 100, ct))).WithName("ListImports");
app.MapGet("/api/v1/imports/{id:guid}/rows", async (Guid id, long? after, int? limit, string? classification, ImportService imports, CancellationToken ct) =>
{
    var pageSize = Math.Clamp(limit ?? 200, 1, 500);
    var rows = await imports.ListRowsAsync(id, Math.Max(0, after ?? 0), pageSize, classification, ct);
    return Results.Ok(rows);
}).WithName("ListImportRows");
app.MapGet("/api/v1/imports/{id:guid}", async (Guid id, ImportService imports, CancellationToken ct) =>
{
    var result = await imports.GetAsync(id, ct);
    return result is null ? Results.Problem(title: "Import not found", statusCode: StatusCodes.Status404NotFound) : Results.Ok(result);
}).WithName("GetImport");
app.MapGet("/api/v1/votes", async (string? search, int? session, DateOnly? fromDate, DateOnly? toDate, int? offset, int? limit, VoteService votes, CancellationToken ct) =>
    Results.Ok(await votes.ListAsync(search, session, fromDate, toDate, offset ?? 0, limit ?? 100, ct))).WithName("ListVotes");
app.MapGet("/api/v1/vote", async (string voteId, VoteService votes, CancellationToken ct) =>
{
    var vote = await votes.GetAsync(voteId, ct);
    return vote is null ? Results.Problem(title: "Vote not found", statusCode: StatusCodes.Status404NotFound) : Results.Ok(vote);
}).WithName("GetVote");
app.MapPut("/api/v1/vote/position", async (string voteId, PositionUpdateRequest request, HttpContext context, VoteService votes, CancellationToken ct) =>
{
    var user = (DemoUser)context.Items["DemoUser"]!;
    if (user.Role != "Admin") return Results.Problem(title: "Administrator required", statusCode: StatusCodes.Status403Forbidden);
    var result = await votes.SetPositionAsync(voteId, request, user.UserId, context.TraceIdentifier, ct);
    if (result.NotFound) return Results.Problem(title: "Vote not found", statusCode: StatusCodes.Status404NotFound);
    if (!result.Updated) return Results.Problem(title: "Invalid position", detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
    return Results.Ok(result.Position);
}).WithName("SetVotePosition");
app.MapPost("/api/v1/snapshots", async (SnapshotRequest request, HttpContext context, SnapshotService snapshots, CancellationToken ct) =>
{
    var user = (DemoUser)context.Items["DemoUser"]!;
    var result = await snapshots.CreateAsync(request, user.UserId, context.TraceIdentifier, ct);
    if (!result.Created)
    {
        var extensions = result.VotesWithoutPosition is null ? null : new Dictionary<string, object?> { ["votesWithoutPosition"] = result.VotesWithoutPosition };
        return Results.Problem(title: "Snapshot selection is invalid", detail: result.Error, statusCode: StatusCodes.Status422UnprocessableEntity, extensions: extensions);
    }
    return Results.Accepted($"/api/v1/snapshots/{result.SnapshotId}", new { id = result.SnapshotId, status = "Queued", selectedVoteCount = result.SelectedVoteCount });
}).WithName("CreateSnapshot");
app.MapGet("/api/v1/snapshots", async (SnapshotService snapshots, CancellationToken ct) => Results.Ok(await snapshots.ListAsync(ct))).WithName("ListSnapshots");
app.MapGet("/api/v1/snapshots/{id:guid}", async (Guid id, SnapshotService snapshots, CancellationToken ct) =>
{
    var snapshot = await snapshots.GetAsync(id, ct);
    return snapshot is null ? Results.Problem(title: "Snapshot not found", statusCode: StatusCodes.Status404NotFound) : Results.Ok(snapshot);
}).WithName("GetSnapshot");
app.MapGet("/api/v1/snapshots/{id:guid}/results", async (Guid id, int? offset, int? limit, SnapshotService snapshots, CancellationToken ct) => Results.Ok(await snapshots.ListResultsAsync(id, offset ?? 0, limit ?? 100, ct))).WithName("ListSnapshotResults");
app.MapGet("/api/v1/snapshots/{id:guid}/countries/{countryCode}/contributions", async (Guid id, string countryCode, int? after, int? limit, SnapshotService snapshots, CancellationToken ct) => Results.Ok(await snapshots.ListContributionsAsync(id, countryCode, after ?? 0, limit ?? 100, ct))).WithName("ListSnapshotContributions");
app.MapPost("/api/v1/snapshots/{id:guid}/publish", async (Guid id, PublishSnapshotRequest request, HttpContext context, SnapshotService snapshots, CancellationToken ct) =>
{
    var user = (DemoUser)context.Items["DemoUser"]!;
    if (user.Role != "Admin") return Results.Problem(title: "Administrator required", statusCode: StatusCodes.Status403Forbidden);
    var result = await snapshots.PublishAsync(id, request.Year, request.Quarter, request.ReplaceActive, user.UserId, context.TraceIdentifier, ct);
    if (result.NotFound) return Results.Problem(title: "Snapshot not found or not publishable", detail: result.Error, statusCode: StatusCodes.Status404NotFound);
    if (!result.Applied) return Results.Problem(title: "Publication conflict", detail: result.Error, statusCode: StatusCodes.Status409Conflict);
    return Results.Ok(new { id, publicationYear = request.Year, publicationQuarter = request.Quarter, status = "Published" });
}).WithName("PublishSnapshot");
app.MapPost("/api/v1/snapshots/{id:guid}/withdraw", async (Guid id, HttpContext context, SnapshotService snapshots, CancellationToken ct) =>
{
    var user = (DemoUser)context.Items["DemoUser"]!;
    if (user.Role != "Admin") return Results.Problem(title: "Administrator required", statusCode: StatusCodes.Status403Forbidden);
    var result = await snapshots.WithdrawAsync(id, user.UserId, context.TraceIdentifier, ct);
    if (result.NotFound) return Results.Problem(title: "Snapshot is not actively published", statusCode: StatusCodes.Status404NotFound);
    return Results.Ok(new { id, status = "Withdrawn" });
}).WithName("WithdrawSnapshot");
app.MapGet("/api/v1/conflicts", async (string? voteId, string? status, ConflictService conflicts, CancellationToken ct) =>
{
    var requestedStatus = string.IsNullOrWhiteSpace(status) ? "Pending" : status.Trim();
    if (requestedStatus is not ("Pending" or "Resolved" or "Accepted" or "Rejected" or "All"))
        return Results.Problem(title: "Invalid conflict status", detail: "Use Pending, Resolved, Accepted, Rejected, or All.", statusCode: StatusCodes.Status400BadRequest);
    return Results.Ok(await conflicts.ListPendingAsync(voteId?.Trim(), requestedStatus, ct));
}).WithName("ListPendingConflicts");
app.MapPost("/api/v1/conflicts/{id:guid}/decision", async (Guid id, ConflictDecision decision, HttpContext context, ConflictService conflicts, CancellationToken ct) =>
{
    var user = (DemoUser)context.Items["DemoUser"]!;
    var result = await conflicts.DecideAsync(id, decision, user.UserId, context.TraceIdentifier, ct);
    if (result.Applied) return Results.Ok(new { id, status = decision.Action.Equals("Accept", StringComparison.OrdinalIgnoreCase) ? "Accepted" : "Rejected" });
    var changed = result.Message?.StartsWith("The conflict was already resolved", StringComparison.Ordinal) == true || result.Message?.StartsWith("The conflict changed", StringComparison.Ordinal) == true;
    return Results.Problem(title: changed ? "Conflict already resolved" : "Invalid decision", detail: result.Message, statusCode: changed ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
}).WithName("DecideConflict");
var seedPath = builder.Configuration["Seed:MemberStatesPath"];
await app.Services.GetRequiredService<DatabaseBootstrapper>().InitializeAsync(seedPath, app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program { }
