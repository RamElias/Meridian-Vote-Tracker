using Meridian.Infrastructure.BackgroundJobs;
using Meridian.Infrastructure.Features.Conflicts.Services;
using Meridian.Infrastructure.Features.Imports.Contracts;
using Meridian.Infrastructure.Features.Imports.Processing;
using Meridian.Infrastructure.Features.Imports.Services;
using Meridian.Infrastructure.Features.Snapshots.Processing;
using Meridian.Infrastructure.Features.Snapshots.Services;
using Meridian.Infrastructure.Features.Votes.Services;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Storage;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

// This runner creates and drops its own uniquely named database. Never resets the supplied database.
var adminString = Environment.GetEnvironmentVariable("MERIDIAN_TEST_CONNECTION_STRING")
    ?? throw new InvalidOperationException("Set MERIDIAN_TEST_CONNECTION_STRING to a PostgreSQL account with CREATEDB permission.");
var database = "meridian_regression_" + Guid.NewGuid().ToString("N");
var storagePath = Path.Combine(Path.GetTempPath(), database);
await using var admin = NpgsqlDataSource.Create(adminString);
await using (var create = admin.CreateCommand($"CREATE DATABASE {database}")) await create.ExecuteNonQueryAsync();
try
{
    var builder = new NpgsqlConnectionStringBuilder(adminString) { Database = database, CommandTimeout = 30 };
    await using var source = NpgsqlDataSource.Create(builder.ConnectionString);
    var bootstrap = new DatabaseBootstrapper(builder.ConnectionString, NullLogger<DatabaseBootstrapper>.Instance);
    await bootstrap.InitializeAsync(null, default);
    if(args is ["--historic",var historicFile,var seedFile])
    {
        await HistoricVolumeChecks.RunAsync(source,bootstrap,storagePath,historicFile,seedFile);
        return;
    }

    await Sql("INSERT INTO member_states VALUES ('AAA','Alpha'),('BBB','Beta'),('CCC','Gamma'); INSERT INTO membership_periods(country_code,member_from,member_to) VALUES ('AAA','2000-01-01',NULL),('BBB','2020-01-01',NULL),('CCC','2000-01-01','2024-12-31')");
    var storage = new LocalImportFileStorage(storagePath);
    var imports = new ImportService(source, storage);
    var progress = new JobProgressReporter(source);
    var worker = new CsvImportJobProcessor(source, new CsvImportValidator(source, progress), storage, progress, NullLogger<CsvImportJobProcessor>.Instance);
    var conflicts = new ConflictService(source);
    var votes = new VoteService(source);
    var snapshots = new SnapshotService(source);
    var rankings = new RankingSnapshotJobProcessor(source, progress, NullLogger<RankingSnapshotJobProcessor>.Instance);

    // Interleaved votes, duplicate row, strict ballot, membership and partial acceptance.
    var rows = new[] { Row("v1","AAA","Y"), Row("v2","AAA","N"), Row("v1","BBB","A"), Row("v1","AAA","Y"), Row("bad","AAA","YN"), Row("old","BBB","Y","2019-01-01") };
    var first = await Import("initial.csv", rows);
    Check(await Count($"SELECT (report->'summary'->>'New')::bigint FROM imports WHERE id='{first}'") == 3, "partial import / interleaved New count");
    Check(await Count($"SELECT (report->'summary'->>'Invalid')::bigint FROM imports WHERE id='{first}'") == 2, "invalid ballot and membership stay out of approved ballots");
    Check(await Count($"SELECT (report->'summary'->>'Duplicate')::bigint FROM imports WHERE id='{first}'") == 1, "exact row duplicate");
    var duplicate = await Upload("same.csv", rows);
    Check(duplicate.Status == "Duplicate" && duplicate.OriginalImportId == first, "identical file records history without job");
    Check((await imports.ListRowsAsync(duplicate.ImportId,0,100,null,default)).Count == 0, "duplicate history rows endpoint with null filter");
    var overlap = await Import("overlap.csv", [Row("v1","AAA","Y", name:"Renamed Alpha"),Row("v2","CCC","X")]);
    Check(await Count($"SELECT (report->'summary'->>'NoChange')::bigint FROM imports WHERE id='{overlap}'") == 1, "country rename is NoChange");
    var contradiction = await Import("contradiction.csv", [Row("contradict","AAA","Y"),Row("contradict","AAA","N")]);
    Check(await Count($"SELECT (report->'summary'->>'Contradiction')::bigint FROM imports WHERE id='{contradiction}'") == 2, "contradictory ballots rejected together");

    var boundaries=await Import("membership-boundaries.csv", [Row("last-day","CCC","Y","2024-12-31"),Row("after-last-day","CCC","Y","2025-01-01"),Row("first-day","BBB","Y","2020-01-01")]);
    Check(await Count($"SELECT (report->'summary'->>'New')::bigint FROM imports WHERE id='{boundaries}'")==2,"membership start/end are inclusive");
    Check(await Count("SELECT count(*) FROM votes WHERE assembly_vote_id='after-last-day'")==0,"non-member-only rows cannot create approved votes");
    var change = await Import("change.csv", [Row("v1","AAA","N")]);
    await Import("second-source.csv", [Row("v1","AAA","X")]);
    var open = JsonSerializer.SerializeToElement(await conflicts.ListPendingAsync("v1","Pending",default));
    Check(open.GetArrayLength() == 1 && open[0].GetProperty("proposals").GetArrayLength() == 2, "sources merge into one conflict");
    var conflictId = open[0].GetProperty("id").GetGuid();
    var displayed = open[0].GetProperty("proposals")[1].GetProperty("value").GetProperty("ballot").GetString();
    var decisions = await Task.WhenAll(Enumerable.Range(0,2).Select(_ => conflicts.DecideAsync(conflictId,new("Accept",1,"Regression"),"lana","regression",default)));
    Check(decisions.Count(d=>d.Applied) == 1, "concurrent conflict decisions: exactly one wins");
    Check(await Text("SELECT vv.ballot FROM country_votes v JOIN country_vote_versions vv ON vv.id=v.current_version_id WHERE v.assembly_vote_id='v1' AND v.country_code='AAA'") == displayed, "displayed proposal index is the approved proposal");
    var detail = JsonSerializer.SerializeToElement(await votes.GetAsync("v1",default));
    Check(detail.GetProperty("history").EnumerateArray().Any(x=>x.GetProperty("action").GetString()=="CountryBallotImported" && x.GetProperty("sourceFileName").GetString()=="initial.csv"), "timeline includes original import provenance");

    var normalized = await Import("normalized.csv", [" normalized ,0077, 2024-01-01 , Normalized title , AAA , Alpha , Y , 2024-01-02T00:00:00Z "]);
    Check(await Count($"SELECT (report->>'normalizedRows')::bigint FROM imports WHERE id='{normalized}'") == 1,"normalization is reported");
    Check(await Count($"SELECT count(*) FROM import_staging_rows WHERE import_id='{normalized}' AND raw_values->>'ballot'=' Y ' AND normalized_values->>'ballot'='Y'") == 1,"raw and normalized source values kept separately");
    var normalizedOverlap = await Import("normalized-overlap.csv", ["normalized,77,2024-01-01,Normalized title,AAA,Alpha,Y,2024-01-02T00:00:00Z"]);
    Check(await Count($"SELECT (report->'summary'->>'NoChange')::bigint FROM imports WHERE id='{normalizedOverlap}'") == 1,"comparison uses the same normalized values as validation and storage");
    // Date decision changes membership. Reports must be refreshed in the same transaction.
    await Import("date-base.csv", [Row("date","AAA","Y","2019-01-01")]);
    var deferred = await Import("date-change.csv", [Row("date","BBB","Y","2024-01-01")]);
    var deferred2 = await Import("date-change-2.csv", [Row("date","BBB","N","2024-01-01")]);
    Check(await Count($"SELECT (report->'summary'->>'Conflict')::bigint FROM imports WHERE id='{deferred}'") == 1, "date-dependent row waits for decision");
    var dateConflict = await Id("SELECT id FROM conflicts WHERE assembly_vote_id='date' AND target_type='VoteMetadata' AND status='Pending'");
    Check((await conflicts.DecideAsync(dateConflict,new("Accept",0,null),"lana","regression",default)).Applied, "date decision accepted");
    foreach (var id in new[]{deferred,deferred2})
        Check(await Count($"SELECT count(*) FROM imports i WHERE i.id='{id}' AND (SELECT jsonb_object_agg(classification,n) FROM (SELECT classification,count(*) n FROM import_staging_rows WHERE import_id=i.id GROUP BY classification) s)=i.report->'summary'") == 1,"report matches rows after date decision");
    Check(await Count("SELECT count(*) FROM import_staging_rows WHERE raw_values->>'assembly_vote_id'='date' AND classification IS NULL") == 0,"all deferred sources classified");
    Check(await Count("SELECT count(*) FROM conflicts WHERE assembly_vote_id='date' AND target_type='CountryVote' AND status='Pending'") == 1,"different deferred ballots remain a human conflict");
    await Import("reject-date-base.csv", [Row("reject-date","AAA","Y","2019-01-01")]);
    var rejected = await Import("reject-date-change.csv", [Row("reject-date","BBB","Y")]);
    var rejectId = await Id("SELECT id FROM conflicts WHERE assembly_vote_id='reject-date' AND status='Pending'");
    await conflicts.DecideAsync(rejectId,new("Reject",null,null),"lana","regression",default);
    Check(await Count($"SELECT (report->'summary'->>'Invalid')::bigint FROM imports WHERE id='{rejected}'") == 1,"rejected date updates deferred report to Invalid");

    // Stop a running worker inside the final DB update, after promotion SQL has executed.
    await Sql("CREATE FUNCTION delay_import_completion() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.file_name='crash.csv' AND NEW.status='Completed' THEN PERFORM pg_sleep(30); END IF; RETURN NEW; END $$; CREATE TRIGGER delay_import BEFORE UPDATE ON imports FOR EACH ROW EXECUTE FUNCTION delay_import_completion()");
    var crash = await Upload("crash.csv", [Row("crash","AAA","Y")]);
    using (var stop = new CancellationTokenSource())
    {
        var running = worker.ProcessNextAsync(stop.Token);
        await WaitForSleep();
        stop.Cancel();
        try { await running; throw new Exception("Worker should have been cancelled"); } catch (OperationCanceledException) { }
    }
    Check(await Count("SELECT count(*) FROM votes WHERE assembly_vote_id='crash'") == 0,"cancelled promotion rolls back approved data");
    await Sql("DROP TRIGGER delay_import ON imports; DROP FUNCTION delay_import_completion()");
    await bootstrap.RecoverInterruptedJobsAsync(default);
    await worker.ProcessNextAsync(default);
    Check(await Count($"SELECT (report->'summary'->>'New')::bigint FROM imports WHERE id='{crash.ImportId}' AND status='Completed'") == 1,"recovered import preserves original New classification");
    Check(await Count($"SELECT count(*) FROM background_jobs WHERE payload->>'importId'='{crash.ImportId}' AND status='Completed'") == 1,"import and job complete together");

    // >100 votes exercises multiple progress updates. Exact metric fixture is isolated by explicit IDs.
    await Import("metrics.csv", [Row("m1","AAA","Y"),Row("m2","AAA","N"),Row("m3","AAA","A"),Row("m4","AAA","X")]);
    for (var n=1;n<=4;n++) Check((await votes.SetPositionAsync($"m{n}",new("Y",n==1?2:1,null),"lana","regression",default)).Updated,"position set");
    Check(!(await votes.SetPositionAsync("m1",new("A",9,null),"lana","regression",default)).Updated,"position validation");
    var stale=await votes.SetPositionAsync("m1",new("N",5,null,0),"lana","regression",default);
    Check(stale.Stale && stale.Position is not null,"stale position rejected with current state");
    var missingIds=await snapshots.CreateAsync(new(null,null,null,["m1","does-not-exist"]),"lana","regression",default);
    Check(!missingIds.Created && missingIds.UnknownVoteIds!.SequenceEqual(new[]{"does-not-exist"}),"unknown explicit snapshot IDs are returned");
    Check(!(await snapshots.CreateAsync(new(null,null,null,["crash"]),"lana","regression",default)).Created,"missing positions prevent snapshot");
    var snap = await snapshots.CreateAsync(new(null,null,null,["m1","m2","m3","m4"]),"lana","regression",default);
    Check(snap.Created,"snapshot queued");
    await rankings.ProcessNextAsync(default);
    var snapId=snap.SnapshotId!.Value;
    Check(await Text($"SELECT status FROM ranking_snapshots WHERE id='{snapId}'") == "Completed","snapshot completed");
    Check(await Count($"SELECT count(*) FROM ranking_snapshot_results WHERE snapshot_id='{snapId}' AND country_code='AAA' AND round(alignment_score,1)=66.7 AND participation_percent=75 AND coverage_percent=100") == 1,"weighted alignment 66.7 / participation 75 / coverage 100");
    Check(await Count($"SELECT count(*) FROM ranking_snapshot_results WHERE snapshot_id='{snapId}' AND country_code='BBB' AND alignment_score IS NULL AND participation_percent IS NULL AND coverage_percent=0") == 1,"no denominator is N/A, missing records have zero coverage");
    var frozen = await Text($"SELECT jsonb_agg(to_jsonb(c) ORDER BY country_code,assembly_vote_id)::text FROM ranking_snapshot_contributions c WHERE snapshot_id='{snapId}'");
    await votes.SetPositionAsync("m1",new("N",5,null,await Count("SELECT current_version_id FROM vote_positions WHERE assembly_vote_id='m1'")),"lana","regression",default);
    await Import("metrics-change.csv", [Row("m1","AAA","N")]);
    var mConflict=await Id("SELECT id FROM conflicts WHERE assembly_vote_id='m1' AND status='Pending'");
    await conflicts.DecideAsync(mConflict,new("Accept",0,null),"lana","regression",default);
    Check(frozen == await Text($"SELECT jsonb_agg(to_jsonb(c) ORDER BY country_code,assembly_vote_id)::text FROM ranking_snapshot_contributions c WHERE snapshot_id='{snapId}'"),"snapshot unchanged after ballot/position edits");
    // Simulate process exit after snapshot commit but before final job acknowledgement.
    await Sql($"UPDATE background_jobs SET status='Running' WHERE id='{snapId}'");
    await bootstrap.RecoverInterruptedJobsAsync(default);
    Check(await Text($"SELECT status FROM background_jobs WHERE id='{snapId}'") == "Completed","recovery acknowledges committed snapshot without recalculation");
    var interrupted = await snapshots.CreateAsync(new(null,null,null,["m1"]),"lana","regression",default);
    var interruptedId = interrupted.SnapshotId!.Value;
    await Sql($"CREATE FUNCTION delay_snapshot_completion() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.id='{interruptedId}' AND NEW.status='Completed' THEN PERFORM pg_sleep(30); END IF; RETURN NEW; END $$; CREATE TRIGGER delay_snapshot BEFORE UPDATE ON ranking_snapshots FOR EACH ROW EXECUTE FUNCTION delay_snapshot_completion()");
    using (var stop = new CancellationTokenSource())
    {
        var running = rankings.ProcessNextAsync(stop.Token);
        await WaitForSleep();
        stop.Cancel();
        try { await running; throw new Exception("Snapshot worker should have been cancelled"); } catch (OperationCanceledException) { }
    }
    Check(await Count($"SELECT count(*) FROM ranking_snapshot_contributions WHERE snapshot_id='{interruptedId}'") == 0,"cancelled snapshot exposes no partial contributions");
    await Sql("DROP TRIGGER delay_snapshot ON ranking_snapshots; DROP FUNCTION delay_snapshot_completion()");
    await bootstrap.RecoverInterruptedJobsAsync(default);
    await rankings.ProcessNextAsync(default);
    Check(await Text($"SELECT status FROM ranking_snapshots WHERE id='{interruptedId}'") == "Completed","interrupted snapshot recovers and completes");
    await MustReject("UPDATE vote_versions SET resolution_title='tampered' WHERE assembly_vote_id='m1'", "approved vote versions immutable");
    await MustReject("DELETE FROM audit_events WHERE entity_key='m1'", "audit immutable");
    await MustReject($"UPDATE ranking_snapshot_contributions SET ballot='N' WHERE snapshot_id='{snapId}'", "completed contributions immutable");
    await MustReject($"DELETE FROM ranking_snapshot_results WHERE snapshot_id='{snapId}'", "completed snapshot results immutable");
    var other=await snapshots.CreateAsync(new(null,null,null,["m1"]),"lana","regression",default);
    await rankings.ProcessNextAsync(default);
    var publications=await Task.WhenAll(snapshots.PublishAsync(snapId,2024,1,false,"lana","regression",default),snapshots.PublishAsync(other.SnapshotId!.Value,2024,1,false,"lana","regression",default));
    Check(publications.Count(x=>x.Applied)==1,"concurrent publication: one active snapshot per quarter");
    Check((await snapshots.PublishAsync(other.SnapshotId.Value,2024,1,true,"lana","regression",default)).Applied,"explicit publication replacement");
    Check((await snapshots.WithdrawAsync(other.SnapshotId.Value,"lana","regression",default)).Applied,"withdrawal retains history");

    await PdfImportChecks.RunAsync(source,bootstrap,storage,worker);
    await HttpApiChecks.RunAsync(builder.ConnectionString,source,first,snapId);
    await WorkerHostChecks.RunAsync(builder.ConnectionString,source,storagePath);

    var scaleWatch = System.Diagnostics.Stopwatch.StartNew();
    var size = args.Contains("--large") ? 6500 : 101;
    if (args.Contains("--large"))
    {
        // 150 members x 6,500 votes = 975,000 frozen contributions.
        await Sql("INSERT INTO member_states SELECT 'Z'||chr(65+i/26)||chr(65+i%26),'Scale country '||i FROM generate_series(0,146) i; INSERT INTO membership_periods(country_code,member_from) SELECT country_code,'2000-01-01' FROM member_states WHERE country_code LIKE 'Z%'");
    }
    await Import("scale.csv",Enumerable.Range(1,size).Select(n=>Row($"scale-{n}","AAA","Y")).ToArray());
    await Sql("INSERT INTO vote_positions(assembly_vote_id) SELECT assembly_vote_id FROM votes WHERE assembly_vote_id LIKE 'scale-%'; WITH p AS (INSERT INTO position_versions(assembly_vote_id,stance,weight,actor_user_id) SELECT assembly_vote_id,'Y',1,'lana' FROM votes WHERE assembly_vote_id LIKE 'scale-%' RETURNING id,assembly_vote_id) UPDATE vote_positions v SET current_version_id=p.id FROM p WHERE p.assembly_vote_id=v.assembly_vote_id");
    var scale=await snapshots.CreateAsync(new(null,new DateOnly(2025,1,1),new DateOnly(2025,1,1),null),"lana","regression",default);
    Check(scale.Created,"scale snapshot queued");
    if(!args.Contains("--large")) await Sql("CREATE FUNCTION pause_scale_completion() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.status='Completed' AND (NEW.selection->>'FromDate') IS NOT NULL THEN PERFORM pg_sleep(1); END IF; RETURN NEW; END $$; CREATE TRIGGER pause_scale BEFORE UPDATE ON ranking_snapshots FOR EACH ROW EXECUTE FUNCTION pause_scale_completion()");
    using(var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(10)))
    {
        var calculation=rankings.ProcessNextAsync(timeout.Token);
        var observed=false;
        while (!calculation.IsCompleted)
        {
            observed |= await Count($"SELECT count(*) FROM job_progress WHERE job_id='{scale.SnapshotId}' AND phase='Calculating' AND current_count>0") > 0;
            await Task.Delay(100,timeout.Token);
        }
        await calculation;
        Check(observed,"progress visible on independent connection during calculation");
    }
    Check(await Text($"SELECT status FROM ranking_snapshots WHERE id='{scale.SnapshotId}'")=="Completed","multi-batch snapshot progress does not self-deadlock");
    Check(await Count($"SELECT count(*) FROM ranking_snapshot_contributions WHERE snapshot_id='{scale.SnapshotId}'")==size*(args.Contains("--large")?150:3),"all scale contributions persisted");
    Console.WriteLine($"PASS: integration suite; scale={size} votes; scale import+calculation={scaleWatch.Elapsed.TotalSeconds:F1}s; isolated database removed on exit.");

    async Task MustReject(string sql,string message) { try { await Sql(sql); } catch(PostgresException ex) when(ex.SqlState=="55000") { Check(true,message); return; } throw new Exception("FAIL: "+message); }
    async Task Sql(string sql) { await using var c=source.CreateCommand(sql); c.CommandTimeout=180; await c.ExecuteNonQueryAsync(); }
    async Task<long> Count(string sql) { await using var c=source.CreateCommand(sql); return Convert.ToInt64(await c.ExecuteScalarAsync()); }
    async Task<string?> Text(string sql) { await using var c=source.CreateCommand(sql); return (await c.ExecuteScalarAsync())?.ToString(); }
    async Task<Guid> Id(string sql) { await using var c=source.CreateCommand(sql); return (Guid)(await c.ExecuteScalarAsync())!; }
    async Task<CreatedImport> Upload(string name,string[] lines) { using var stream=new MemoryStream(Encoding.UTF8.GetBytes("assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at\n"+string.Join('\n',lines)+"\n")); return await imports.CreateAsync(name,"regression","dana",stream,default); }
    async Task<Guid> Import(string name,string[] lines) { var i=await Upload(name,lines); Check(i.Status=="Queued",$"{name} queued"); await worker.ProcessNextAsync(default); Check(await Text($"SELECT status FROM imports WHERE id='{i.ImportId}'")=="Completed",$"{name} completed"); return i.ImportId; }
    async Task WaitForSleep() { for(var n=0;n<200;n++) { if(await Count("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event='PgSleep'")>0)return; await Task.Delay(50); } throw new Exception("Completion fault point was not reached"); }
}
finally
{
    NpgsqlConnection.ClearAllPools();
    await using var drop=admin.CreateCommand($"DROP DATABASE {database} WITH (FORCE)");
    await drop.ExecuteNonQueryAsync();
    if(Directory.Exists(storagePath)) Directory.Delete(storagePath,true);
}
static string Row(string vote,string country,string ballot,string date="2024-01-01",string? name=null) => $"{vote},77,{(vote.StartsWith("scale-")?"2025-01-01":date)},Resolution {vote},{country},{name??country},{ballot},2024-01-02T00:00:00Z";
static void Check(bool condition,string message) { if(!condition) throw new Exception("FAIL: "+message); Console.WriteLine("PASS: "+message); }
