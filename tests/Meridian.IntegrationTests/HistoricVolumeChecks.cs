using System.Diagnostics;
using System.Text.Json;
using Meridian.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

internal static class HistoricVolumeChecks
{
    public static async Task RunAsync(NpgsqlDataSource source,DatabaseBootstrapper bootstrap, string storagePath,string file,string seed)
    {
        await bootstrap.InitializeAsync(seed,default);
        var progress=new JobProgressReporter(source);
        var storage=new LocalImportFileStorage(storagePath);
        var imports=new ImportService(source,storage);
        var processor=new CsvImportJobProcessor(source,new CsvImportValidator(source,progress),storage,progress,NullLogger<CsvImportJobProcessor>.Instance);
        var watch=Stopwatch.StartNew();
        await using var input=File.OpenRead(file);
        var import=await imports.CreateAsync(Path.GetFileName(file),"historic-volume","dana",input,default);
        await processor.ProcessNextAsync(default);
        await using var result=source.CreateCommand("SELECT status,rows_total,report::text FROM imports WHERE id=@id");result.Parameters.AddWithValue("id",import.ImportId);
        await using(var reader=await result.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            if(reader.GetString(0)!="Completed") throw new Exception("Historic import failed: "+reader.GetString(2));
            using var report=JsonDocument.Parse(reader.GetString(2));
            Console.WriteLine($"PASS: supplied historic CSV; rows={reader.GetInt64(1)}; upload+staging+validation+atomic promotion/report={watch.Elapsed.TotalSeconds:F1}s; summary={report.RootElement.GetProperty("summary")}");
        }
        await using(var positions=source.CreateCommand("INSERT INTO vote_positions(assembly_vote_id) SELECT assembly_vote_id FROM votes; WITH p AS (INSERT INTO position_versions(assembly_vote_id,stance,weight,actor_user_id) SELECT assembly_vote_id,'Y',1,'lana' FROM votes RETURNING id,assembly_vote_id) UPDATE vote_positions v SET current_version_id=p.id FROM p WHERE p.assembly_vote_id=v.assembly_vote_id")) { positions.CommandTimeout=600;await positions.ExecuteNonQueryAsync(); }
        var created=await new SnapshotService(source).CreateAsync(new(null,new DateOnly(1900,1,1),new DateOnly(2100,1,1),null),"lana","historic-volume",default);
        if(!created.Created) throw new Exception(created.Error);
        watch.Restart();
        await new RankingSnapshotJobProcessor(source,progress,NullLogger<RankingSnapshotJobProcessor>.Instance).ProcessNextAsync(default);
        await using var check=source.CreateCommand("SELECT status,(SELECT count(*) FROM ranking_snapshot_contributions WHERE snapshot_id=@id),(SELECT count(*) FROM ranking_snapshot_votes WHERE snapshot_id=@id)*(SELECT count(*) FROM member_states) FROM ranking_snapshots WHERE id=@id");check.Parameters.AddWithValue("id",created.SnapshotId!.Value);
        await using var final=await check.ExecuteReaderAsync();await final.ReadAsync();
        if(final.GetString(0)!="Completed" || final.GetInt64(1)!=final.GetInt64(2)) throw new Exception("Historic snapshot did not complete with every contribution");
        Console.WriteLine($"PASS: supplied historic snapshot; selectedVotes={created.SelectedVoteCount}; contributions={final.GetInt64(1)}; calculation={watch.Elapsed.TotalSeconds:F1}s");
    }
}
