using System.Collections.Concurrent;
using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;
namespace Meridian.Infrastructure.BackgroundJobs;

// Uses a separate, short transaction against its own row. Work transactions never
// update this table, so live progress does not wait on promotion/calculation locks.
public sealed class JobProgressReporter(NpgsqlDataSource dataSource)
{
    private readonly ConcurrentDictionary<Guid,(string Phase,long At,long Current)> lastWrite = new();
    public async Task ReportAsync(Guid jobId, string phase, long current, long? total, CancellationToken ct, bool force = false, string unit = "rows")
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && lastWrite.TryGetValue(jobId, out var previous) && previous.Phase == phase && !(previous.Current==0 && current>0) && Stopwatch.GetElapsedTime(previous.At,now) < TimeSpan.FromMilliseconds(500)) return;
        await using var command = dataSource.CreateCommand("INSERT INTO job_progress(job_id,phase,current_count,total_count,unit) VALUES (@id,@phase,@current,@total,@unit) ON CONFLICT(job_id) DO UPDATE SET phase=EXCLUDED.phase,current_count=EXCLUDED.current_count,total_count=CASE WHEN job_progress.phase=EXCLUDED.phase THEN COALESCE(EXCLUDED.total_count,job_progress.total_count) ELSE EXCLUDED.total_count END,unit=EXCLUDED.unit,updated_at=now()");
        command.Parameters.AddWithValue("id",jobId);
        command.Parameters.AddWithValue("phase",phase);
        command.Parameters.AddWithValue("current",current);
        command.Parameters.Add("total",NpgsqlDbType.Bigint).Value=(object?)total ?? DBNull.Value;
        command.Parameters.AddWithValue("unit",unit);
        await command.ExecuteNonQueryAsync(ct);
        lastWrite[jobId]=(phase,now,current);
        if (phase is "Completed" or "Failed") lastWrite.TryRemove(jobId,out _);
    }
}
