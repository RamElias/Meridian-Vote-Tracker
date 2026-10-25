using System.Text;
using Meridian.Infrastructure.Features.Imports.Services;
using Meridian.Infrastructure.Features.Imports.Processing;
using Meridian.Infrastructure.Features.Votes.Services;
using Meridian.Infrastructure.Features.Snapshots.Services;
using Meridian.Infrastructure.Features.Snapshots.Processing;
using Meridian.Infrastructure.Features.Conflicts.Services;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Validation;
using Npgsql;

internal static class ReviewRegressionChecks
{
    public static async Task RunAsync(NpgsqlDataSource source, ImportService imports, CsvImportJobProcessor csv,
        VoteService votes, SnapshotService snapshots, RankingSnapshotJobProcessor rankings, DatabaseBootstrapper bootstrap)
    {
        const string header = "assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at\n";
        string Row(string id, string title, string ballot = "Y", string date = "2019-01-01", string country = "AAA") =>
            $"{id},77,{date},{title},{country},Alpha,{ballot},2024-01-02T00:00:00Z";
        async Task<Guid> Import(string name, string body)
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(header + body));
            var queued = await imports.CreateAsync(name, "review-regression", "dana", stream, default);
            await csv.ProcessNextAsync(default);
            Check((await imports.GetAsync(queued.ImportId, default))!.Status == "Completed", name + " completes with row-level outcomes");
            return queued.ImportId;
        }
        async Task Sql(string text) { await using var command = source.CreateCommand(text); command.CommandTimeout = 90; await command.ExecuteNonQueryAsync(); }
        async Task<long> Count(string text) { await using var command = source.CreateCommand(text); return Convert.ToInt64(await command.ExecuteScalarAsync()); }

        var malformed = await Import("review-csv-syntax.csv", string.Join('\n', new[] {
            Row("review-valid-before", "Before"), Row("review-bad-quote", "bad\"title\""),
            Row("review-bad-tail", "\"Title\"junk"), Row("review-multiline", "\"First line\nsecond line\""),
            Row("review-valid-after", "After"), Row("review-unterminated", "\"unclosed") }));
        var rows = await imports.ListRowsAsync(malformed, 0, 100, null, default);
        Check(rows.Count == 6 && rows.Count(row => row.Classification == "Invalid") == 3, "strict CSV records reject quote syntax and retain valid multiline records");
        Check(rows.Single(row => row.RowNumber == 3).Values.GetProperty("__source_record").GetString()!.Contains("bad\"title\""), "malformed CSV source is preserved without silent quote removal");
        Check(await Count("SELECT count(*) FROM votes WHERE assembly_vote_id IN ('review-valid-before','review-valid-after','review-multiline')") == 3, "valid records before and after recoverable malformed CSV are promoted");
        Check(SourceTimestamp.IsValid("2024-04-01T00:00:00Z") && SourceTimestamp.IsValid("2024-04-01T03:00:00+03:00") && !SourceTimestamp.IsValid("2024-04-01T00:00Z"), "shared source timestamp requires seconds and explicit zone");

        await Import("review-metadata-original.csv", Row("review-metadata", "Original"));
        var changed = await Import("review-metadata-title.csv", Row("review-metadata", "Changed") + "\n" + Row("review-metadata", "Changed", country: "CCC"));
        rows = await imports.ListRowsAsync(changed, 0, 100, "Conflict", default);
        Check(rows.Count == 2 && rows.All(row => row.MetadataConflictId.HasValue), "metadata-only conflicts agree with row filter and report");
        Check(rows.Any(row => row.BallotClassification == "New") && rows.Any(row => row.BallotClassification == "NoChange"), "metadata review retains separate independent ballot outcomes");
        Check(await Count("SELECT count(*) FROM country_votes WHERE assembly_vote_id='review-metadata'") == 2, "metadata report correction does not block independent valid ballot additions");
        var session = await Import("review-metadata-session.csv", Row("review-metadata", "Original").Replace(",77,", ",78,"));
        Check((await imports.ListRowsAsync(session, 0, 100, "Conflict", default)).Count == 1, "session-only change is classified as a metadata conflict");

        await Import("review-capture-original.csv", Row("review-capture", "Capture"));
        var firstPosition = await votes.SetPositionAsync("review-capture", new("Y", 1, null), "lana", "review", default);
        var queued = await snapshots.CreateAsync(new(null, null, null, ["review-capture"]), "dana", "review", default);
        var filtered = await snapshots.CreateAsync(new(null, new DateOnly(2019,1,1), new DateOnly(2019,1,1), ["review-capture"]), "dana", "review", default);
        await Import("review-capture-change.csv", Row("review-capture", "Capture", "N", "2024-01-01"));
        var conflicts = new ConflictService(source);
        foreach (var conflict in (await conflicts.ListPendingAsync("review-capture", "Pending", default)).OrderBy(item => item.TargetType))
            Check((await conflicts.DecideAsync(conflict.Id, new("Accept", 0, null), "dana", "review", default)).Applied, "queued snapshot concurrent correction is approved");
        await votes.SetPositionAsync("review-capture", new("N", 5, null, firstPosition.Position!.VersionId), "lana", "review", default);
        await rankings.ProcessNextAsync(default);
        await rankings.ProcessNextAsync(default);
        Check((await snapshots.GetAsync(filtered.SnapshotId!.Value, default))!.Status == "Failed", "worker re-evaluates date filters at the common capture point");
        var id = queued.SnapshotId!.Value;
        Check(await Count($"SELECT count(*) FROM ranking_snapshot_votes v JOIN ranking_snapshot_contributions c USING(snapshot_id,assembly_vote_id) WHERE v.snapshot_id='{id}' AND c.country_code='AAA' AND v.vote_date='2024-01-01' AND v.stance='N' AND v.weight=5 AND c.ballot='N' AND c.alignment_numerator=5") == 1, "worker capture uses consistent current date, position and ballot after enqueue");
        Check((await snapshots.GetAsync(id, default))!.InputsCapturedAt.HasValue, "snapshot exposes the actual worker capture timestamp");
        var excluded = await snapshots.CreateAsync(new(null, new(2019,1,1), new(2019,1,1), ["review-metadata"]), "dana", "review", default);
        // This selection has no position and must still fail synchronously.
        Check(!excluded.Created, "request preflight still reports missing positions");

        var interrupted = await snapshots.CreateAsync(new(null, null, null, ["review-capture"]), "dana", "review", default);
        await Sql($"CREATE FUNCTION pause_review_snapshot() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.id='{interrupted.SnapshotId}' AND NEW.status='Completed' THEN PERFORM pg_sleep(30); END IF; RETURN NEW; END $$; CREATE TRIGGER pause_review_snapshot BEFORE UPDATE ON ranking_snapshots FOR EACH ROW EXECUTE FUNCTION pause_review_snapshot()");
        using (var stop = new CancellationTokenSource())
        {
            var running = rankings.ProcessNextAsync(stop.Token);
            var reached = false;
            for (var n=0; n<200 && !running.IsCompleted; n++)
            {
                if (await Count("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event='PgSleep'") > 0) { reached=true; break; }
                await Task.Delay(50);
            }
            Check(reached, "snapshot recovery fault reached after capture");
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) { }
        }
        Check(await Count($"SELECT count(*) FROM ranking_snapshot_contributions WHERE snapshot_id='{interrupted.SnapshotId}'") == 0, "interrupted capture and results roll back together");
        await Sql("DROP TRIGGER pause_review_snapshot ON ranking_snapshots; DROP FUNCTION pause_review_snapshot()");
        var current = (await votes.GetAsync("review-capture", default))!.Vote.Position!;
        await votes.SetPositionAsync("review-capture", new("Y", 2, null, current.VersionId), "lana", "review", default);
        await bootstrap.RecoverInterruptedJobsAsync(default);
        await rankings.ProcessNextAsync(default);
        Check(await Count($"SELECT count(*) FROM ranking_snapshot_votes WHERE snapshot_id='{interrupted.SnapshotId}' AND stance='Y' AND weight=2") == 1, "recovered snapshot recaptures all inputs consistently for its new attempt");
        Check(await Count($"SELECT count(*) FROM ranking_snapshot_votes WHERE snapshot_id='{id}' AND stance='N' AND weight=5") == 1, "completed prior snapshot stays immutable after recovery and edits");

        Check((await snapshots.PublishAsync(id, 2030, 2, false, "lana", "review", default)).Applied, "history fixture snapshot published");
        await Sql("WITH jobs AS (INSERT INTO background_jobs(id,job_type,status,payload) SELECT gen_random_uuid(),'ranking_snapshot','Failed','{}'::jsonb FROM generate_series(1,205) RETURNING id) INSERT INTO ranking_snapshots(id,status,selection,calculation_rule_version,created_by_user_id,created_at) SELECT id,'Failed','{}','alignment-v1','dana',now()+interval '1 minute' FROM jobs");
        var first = await snapshots.ListAsync(default, 0, 200);
        var later = await snapshots.ListAsync(default, 200, 200);
        Check(!first.Any(item => item.Id == id) && later.Any(item => item.Id == id), "published snapshot beyond 200 rows is accessible through paging");
        Check((await snapshots.ListAsync(default, year:2030, quarter:2, publication:"Active")).Single().Id == id, "old active snapshot is discoverable by publication period");
        Check(!first.Select(item => item.Id).Intersect(later.Select(item => item.Id)).Any(), "snapshot paging has a stable tie-break order");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception("FAIL: " + message);
        Console.WriteLine("PASS: " + message);
    }
}
