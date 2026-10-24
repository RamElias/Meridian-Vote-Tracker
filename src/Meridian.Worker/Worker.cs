using Meridian.Infrastructure.Features.PdfImports.Processing;
using Meridian.Infrastructure.Features.Imports.Processing;
using Meridian.Infrastructure.Features.Snapshots.Processing;
using Meridian.Infrastructure.Persistence;
using Npgsql;

public sealed class Worker(ILogger<Worker> logger, DatabaseBootstrapper database, CsvImportJobProcessor imports, RankingSnapshotJobProcessor snapshots, PdfExtractionJobProcessor pdfs, NpgsqlDataSource dataSource, IConfiguration configuration) : BackgroundService
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
        var nextQueue = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Verify ownership connection before claiming more work. A lost session stops this Worker.
            await using (var heartbeat = new NpgsqlCommand("SELECT 1", ownership))
                await heartbeat.ExecuteScalarAsync(stoppingToken);
            var didWork = false;
            for (var n = 0; n < 3 && !didWork; n++)
            {
                var queue = (nextQueue + n) % 3;
                didWork = queue switch { 0 => await snapshots.ProcessNextAsync(stoppingToken), 1 => await imports.ProcessNextAsync(stoppingToken), _ => await pdfs.ProcessNextAsync(stoppingToken) };
                if (didWork) nextQueue = (queue + 1) % 3;
            }
            if (didWork) continue;
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
