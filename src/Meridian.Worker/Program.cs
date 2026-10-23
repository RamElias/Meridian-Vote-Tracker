using Meridian.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Configuration.AddKeyVaultIfConfigured(builder.Configuration);
var connectionString = builder.Configuration.GetConnectionString("Meridian")
    ?? throw new InvalidOperationException("ConnectionStrings:Meridian is required.");
builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(provider => new DatabaseBootstrapper(connectionString, provider.GetRequiredService<ILogger<DatabaseBootstrapper>>()));
builder.Services.AddSingleton<IImportFileStorage>(_ => ImportFileStorageRegistration.Create(builder.Configuration));
builder.Services.AddSingleton<JobProgressReporter>();
builder.Services.AddSingleton<CsvImportValidator>();
builder.Services.AddSingleton<CsvImportJobProcessor>();
builder.Services.AddSingleton<RankingSnapshotJobProcessor>();
builder.Services.AddHostedService<Worker>();
if (!string.IsNullOrWhiteSpace(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]) || !string.IsNullOrWhiteSpace(builder.Configuration["ApplicationInsights:ConnectionString"]))
    builder.Services.AddApplicationInsightsTelemetryWorkerService();
var host = builder.Build();
await host.RunAsync();

public sealed class Worker(ILogger<Worker> logger, DatabaseBootstrapper database, CsvImportJobProcessor imports, RankingSnapshotJobProcessor snapshots, NpgsqlDataSource dataSource, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await using var ownership = await dataSource.OpenConnectionAsync(stoppingToken);
        await using (var claim = new NpgsqlCommand("SELECT pg_try_advisory_lock(77492344)", ownership))
        {
            if (await claim.ExecuteScalarAsync(stoppingToken) is not true)
                throw new InvalidOperationException("Another Worker owns this database. This deployment supports one active Worker.");
        }
        await database.InitializeAsync(configuration["Seed:MemberStatesPath"], stoppingToken);
        await database.RecoverInterruptedJobsAsync(stoppingToken);
        logger.LogInformation("Background worker is ready to process persistent jobs.");
        var preferSnapshot = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Verify ownership connection before claiming more work. A lost session stops this Worker.
            await using (var heartbeat = new NpgsqlCommand("SELECT 1", ownership))
                await heartbeat.ExecuteScalarAsync(stoppingToken);
            var didWork = preferSnapshot
                ? await snapshots.ProcessNextAsync(stoppingToken) || await imports.ProcessNextAsync(stoppingToken)
                : await imports.ProcessNextAsync(stoppingToken) || await snapshots.ProcessNextAsync(stoppingToken);
            preferSnapshot = !preferSnapshot;
            if (didWork) continue;
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
