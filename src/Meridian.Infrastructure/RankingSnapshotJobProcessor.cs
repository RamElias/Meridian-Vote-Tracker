using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Infrastructure;

public sealed class RankingSnapshotJobProcessor(NpgsqlDataSource dataSource, ILogger<RankingSnapshotJobProcessor> logger)
{
    private sealed record ClaimedJob(Guid SnapshotId, string CorrelationId);

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var job = await ClaimAsync(cancellationToken);
        if (job is null) return false;
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = job.CorrelationId });
        try
        {
            await CalculateAsync(job.SnapshotId, cancellationToken);
            logger.LogInformation("Completed ranking snapshot {SnapshotId}", job.SnapshotId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Ranking snapshot {SnapshotId} failed", job.SnapshotId);
            await MarkFailedAsync(job.SnapshotId, exception.Message, cancellationToken);
        }
        return true;
    }

    private async Task<ClaimedJob?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        Guid id;
        string correlation;
        await using (var read = new NpgsqlCommand("SELECT j.id,COALESCE(j.payload->>'correlationId','') FROM background_jobs j JOIN ranking_snapshots s ON s.id=j.id WHERE j.job_type='ranking_snapshot' AND j.status='Queued' AND s.status='Queued' ORDER BY j.created_at FOR UPDATE OF j,s SKIP LOCKED LIMIT 1", connection, transaction))
        await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken)) return null;
            id = reader.GetGuid(0);
            correlation = reader.GetString(1);
        }
        await using (var updateJob = new NpgsqlCommand("UPDATE background_jobs SET status='Running',started_at=now() WHERE id=@id", connection, transaction))
        {
            updateJob.Parameters.AddWithValue("id", id);
            await updateJob.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var updateSnapshot = new NpgsqlCommand("UPDATE ranking_snapshots SET status='Running' WHERE id=@id", connection, transaction))
        {
            updateSnapshot.Parameters.AddWithValue("id", id);
            await updateSnapshot.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new ClaimedJob(id, correlation);
    }

    private async Task CalculateAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await using (var clearResults = new NpgsqlCommand("DELETE FROM ranking_snapshot_results WHERE snapshot_id=@id", connection, transaction))
        {
            clearResults.Parameters.AddWithValue("id", snapshotId);
            await clearResults.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var clearContributions = new NpgsqlCommand("DELETE FROM ranking_snapshot_contributions WHERE snapshot_id=@id", connection, transaction))
        {
            clearContributions.Parameters.AddWithValue("id", snapshotId);
            await clearContributions.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var resetProgress = new NpgsqlCommand("UPDATE background_jobs SET progress_current=0 WHERE id=@id AND status='Running'", connection, transaction))
        {
            resetProgress.Parameters.AddWithValue("id", snapshotId);
            await resetProgress.ExecuteNonQueryAsync(cancellationToken);
        }
        var voteIds = new List<string>();
        await using (var selected = new NpgsqlCommand("SELECT assembly_vote_id FROM ranking_snapshot_votes WHERE snapshot_id=@id ORDER BY ordinal", connection, transaction))
        {
            selected.Parameters.AddWithValue("id", snapshotId);
            await using var reader = await selected.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) voteIds.Add(reader.GetString(0));
        }
        if (voteIds.Count == 0) throw new InvalidOperationException("Snapshot has no selected votes.");
        long countryCount;
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM member_states", connection, transaction))
            countryCount = (long)(await count.ExecuteScalarAsync(cancellationToken) ?? 0L);

        await using (var capture = new NpgsqlCommand("UPDATE ranking_snapshots SET inputs_captured_at=transaction_timestamp() WHERE id=@id", connection, transaction))
        {
            capture.Parameters.AddWithValue("id", snapshotId);
            await capture.ExecuteNonQueryAsync(cancellationToken);
        }
        long rowsProcessed = 0;
        foreach (var batch in voteIds.Chunk(100))
        {
            const string insertSql = "WITH base AS (SELECT sv.snapshot_id,sv.assembly_vote_id,sv.stance,sv.weight,sv.vote_date,ms.country_code,ms.country_name,EXISTS(SELECT 1 FROM membership_periods mp WHERE mp.country_code=ms.country_code AND mp.member_from<=sv.vote_date AND (mp.member_to IS NULL OR mp.member_to>=sv.vote_date)) AS member_at_vote,cv.current_version_id AS country_vote_version_id,cvv.ballot FROM ranking_snapshot_votes sv CROSS JOIN member_states ms LEFT JOIN country_votes cv ON cv.assembly_vote_id=sv.assembly_vote_id AND cv.country_code=ms.country_code LEFT JOIN country_vote_versions cvv ON cvv.id=cv.current_version_id WHERE sv.snapshot_id=@id AND sv.assembly_vote_id=ANY(@voteIds)) INSERT INTO ranking_snapshot_contributions(snapshot_id,country_code,country_name,assembly_vote_id,country_vote_version_id,ballot,member_at_vote,alignment_numerator,alignment_denominator,participation_numerator,participation_denominator,coverage_numerator,coverage_denominator) SELECT snapshot_id,country_code,country_name,assembly_vote_id,country_vote_version_id,ballot,member_at_vote,CASE WHEN member_at_vote AND ballot IN ('Y','N') AND ballot=stance THEN weight ELSE 0 END,CASE WHEN member_at_vote AND ballot IN ('Y','N') THEN weight ELSE 0 END,CASE WHEN member_at_vote AND ballot IN ('Y','N','A') THEN 1 ELSE 0 END,CASE WHEN member_at_vote AND ballot IN ('Y','N','A','X') THEN 1 ELSE 0 END,CASE WHEN member_at_vote AND ballot IS NOT NULL THEN 1 ELSE 0 END,CASE WHEN member_at_vote THEN 1 ELSE 0 END FROM base";
            await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
            {
                insert.Parameters.AddWithValue("id", snapshotId);
                insert.Parameters.AddWithValue("voteIds", NpgsqlDbType.Array | NpgsqlDbType.Text, batch);
                rowsProcessed += await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using var progress = dataSource.CreateCommand("UPDATE background_jobs SET progress_current=@current WHERE id=@id AND status='Running'");
            progress.Parameters.AddWithValue("current", rowsProcessed);
            progress.Parameters.AddWithValue("id", snapshotId);
            await progress.ExecuteNonQueryAsync(cancellationToken);
        }
        const string resultsSql = "WITH totals AS (SELECT country_code,max(country_name) AS country_name,sum(alignment_numerator) AS alignment_numerator,sum(alignment_denominator) AS alignment_denominator,sum(participation_numerator) AS participating_ballots,sum(participation_denominator) AS approved_ballots,sum(coverage_numerator) AS approved_member_records,sum(coverage_denominator) AS eligible_member_votes FROM ranking_snapshot_contributions WHERE snapshot_id=@id GROUP BY country_code), scores AS (SELECT *,CASE WHEN alignment_denominator=0 THEN NULL ELSE 100.0*alignment_numerator/alignment_denominator END AS score,CASE WHEN approved_ballots=0 THEN NULL ELSE 100.0*participating_ballots/approved_ballots END AS participation,CASE WHEN eligible_member_votes=0 THEN NULL ELSE 100.0*approved_member_records/eligible_member_votes END AS coverage FROM totals), ranked AS (SELECT *,CASE WHEN score IS NULL THEN NULL ELSE rank() OVER (ORDER BY score DESC NULLS LAST)::integer END AS country_rank FROM scores) INSERT INTO ranking_snapshot_results(snapshot_id,country_code,country_name,rank,alignment_score,alignment_numerator,alignment_denominator,participation_percent,participating_ballots,approved_ballots,coverage_percent,approved_member_records,eligible_member_votes) SELECT @id,country_code,country_name,country_rank,score,alignment_numerator,alignment_denominator,participation,participating_ballots,approved_ballots,coverage,approved_member_records,eligible_member_votes FROM ranked";
        await using (var results = new NpgsqlCommand(resultsSql, connection, transaction))
        {
            results.Parameters.AddWithValue("id", snapshotId);
            await results.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        await using var complete = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var final = await complete.BeginTransactionAsync(cancellationToken);
        await using (var updateSnapshot = new NpgsqlCommand("UPDATE ranking_snapshots SET status='Completed',completed_at=now(),error_message=NULL WHERE id=@id", complete, final))
        {
            updateSnapshot.Parameters.AddWithValue("id", snapshotId);
            await updateSnapshot.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var updateJob = new NpgsqlCommand("UPDATE background_jobs SET status='Completed',progress_current=@current,completed_at=now(),error_message=NULL WHERE id=@id", complete, final))
        {
            updateJob.Parameters.AddWithValue("id", snapshotId);
            updateJob.Parameters.AddWithValue("current", rowsProcessed == 0 ? countryCount * voteIds.Count : rowsProcessed);
            await updateJob.ExecuteNonQueryAsync(cancellationToken);
        }
        await final.CommitAsync(cancellationToken);
    }

    private async Task MarkFailedAsync(Guid id, string message, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var safeMessage = message.Length > 2000 ? message[..2000] : message;
        await using (var updateSnapshot = new NpgsqlCommand("UPDATE ranking_snapshots SET status='Failed',completed_at=now(),error_message=@message WHERE id=@id", connection, transaction))
        {
            updateSnapshot.Parameters.AddWithValue("id", id);
            updateSnapshot.Parameters.AddWithValue("message", safeMessage);
            await updateSnapshot.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var updateJob = new NpgsqlCommand("UPDATE background_jobs SET status='Failed',completed_at=now(),error_message=@message WHERE id=@id", connection, transaction))
        {
            updateJob.Parameters.AddWithValue("id", id);
            updateJob.Parameters.AddWithValue("message", safeMessage);
            await updateJob.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
