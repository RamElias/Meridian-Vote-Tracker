using Meridian.Infrastructure;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Meridian")
    ?? throw new InvalidOperationException("ConnectionStrings:Meridian is required.");
builder.Services.AddSingleton<NpgsqlDataSource>(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(provider => new DatabaseBootstrapper(connectionString, provider.GetRequiredService<ILogger<DatabaseBootstrapper>>()));
builder.Services.AddSingleton<CsvImportValidator>();
builder.Services.AddSingleton<CsvImportJobProcessor>();
builder.Services.AddSingleton<RankingSnapshotJobProcessor>();
builder.Services.AddHostedService<Worker>();
var host = builder.Build();
await host.RunAsync();

public sealed class Worker(ILogger<Worker> logger, DatabaseBootstrapper database, CsvImportJobProcessor imports, RankingSnapshotJobProcessor snapshots, IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await database.InitializeAsync(configuration["Seed:MemberStatesPath"], stoppingToken);
        await database.RecoverInterruptedJobsAsync(stoppingToken);
        logger.LogInformation("Background worker is ready to process persistent jobs.");
        var preferSnapshot = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            var didWork = preferSnapshot
                ? await snapshots.ProcessNextAsync(stoppingToken) || await imports.ProcessNextAsync(stoppingToken)
                : await imports.ProcessNextAsync(stoppingToken) || await snapshots.ProcessNextAsync(stoppingToken);
            preferSnapshot = !preferSnapshot;
            if (didWork) continue;
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
