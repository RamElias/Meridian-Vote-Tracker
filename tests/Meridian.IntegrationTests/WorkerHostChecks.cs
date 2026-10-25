using Meridian.Infrastructure.Features.Imports.Services;
using Meridian.Infrastructure.Storage;
using System.Diagnostics;
using System.Text;
using Npgsql;

internal static class WorkerHostChecks
{
    public static async Task RunAsync(string connectionString,NpgsqlDataSource source,string storagePath)
    {
        var dll=Environment.GetEnvironmentVariable("MERIDIAN_TEST_WORKER_DLL") ?? Path.GetFullPath("src/Meridian.Worker/bin/Debug/net8.0/Meridian.Worker.dll");
        var imports=new ImportService(source,new LocalImportFileStorage(storagePath));
        using var csv=new MemoryStream(Encoding.UTF8.GetBytes("assembly_vote_id,session,vote_date,resolution_title,country_code,country_name,ballot,published_at\nhost-restart,77,2024-01-01,Restart,AAA,Alpha,Y,2024-01-02T00:00:00Z\n"));
        var import=await imports.CreateAsync("worker-host.csv","host-restart","dana",csv,default);
        await Sql("CREATE FUNCTION delay_host_completion() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.file_name='worker-host.csv' AND NEW.status='Completed' THEN PERFORM pg_sleep(30); END IF; RETURN NEW; END $$; CREATE TRIGGER delay_host BEFORE UPDATE ON imports FOR EACH ROW EXECUTE FUNCTION delay_host_completion()");
        var messages=new StringBuilder();
        using var first=Start(messages);
        try
        {
            await Wait(async()=>await Count("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event='PgSleep'")>0, "Worker did not reach the completion fault point");
            using(var second=Start(messages))
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await second.WaitForExitAsync(timeout.Token);
                if(first.HasExited || !messages.ToString().Contains("Another Worker owns this database")) throw new Exception("Additional Worker did not reject ownership");
                if(await Count($"SELECT count(*) FROM imports WHERE id='{import.ImportId}' AND status='Running'")!=1) throw new Exception("Second Worker reset the active import");
            }
            first.Kill(entireProcessTree:true);await first.WaitForExitAsync();
            if(await Count("SELECT count(*) FROM votes WHERE assembly_vote_id='host-restart'")!=0) throw new Exception("Killed worker committed a partial import");
            await Sql("DROP TRIGGER delay_host ON imports; DROP FUNCTION delay_host_completion()");
            using var restarted=Start(messages);
            try
            {
                await Wait(async()=>await Count($"SELECT count(*) FROM imports WHERE id='{import.ImportId}' AND status='Completed'")==1,"Restarted Worker did not recover the interrupted import");
                if(await Count($"SELECT (report->'summary'->>'New')::bigint FROM imports WHERE id='{import.ImportId}'")!=1) throw new Exception("Restarted import lost its original New outcome");
            }
            finally { if(!restarted.HasExited){restarted.Kill(entireProcessTree:true);await restarted.WaitForExitAsync();} }
            Console.WriteLine("PASS: actual Worker process kill/restart, atomic recovery and second-worker ownership rejection.");
        }
        finally { if(!first.HasExited){first.Kill(entireProcessTree:true);await first.WaitForExitAsync();} }

        Process Start(StringBuilder output)
        {
            var info=new ProcessStartInfo("dotnet",dll){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
            info.Environment["ConnectionStrings__Meridian"]=connectionString;
            info.Environment["Azure__KeyVault__VaultUri"]="";
            info.Environment["Storage__LocalPath"]=storagePath;
            info.Environment["Seed__MemberStatesPath"]="";
            var process=Process.Start(info) ?? throw new Exception("Worker could not start");
            process.OutputDataReceived+=(_,e)=>{lock(output) output.AppendLine(e.Data);};
            process.ErrorDataReceived+=(_,e)=>{lock(output) output.AppendLine(e.Data);};
            process.BeginOutputReadLine();process.BeginErrorReadLine();return process;
        }
        async Task Sql(string sql){await using var command=source.CreateCommand(sql);command.CommandTimeout=90;await command.ExecuteNonQueryAsync();}
        async Task<long> Count(string sql){await using var command=source.CreateCommand(sql);return Convert.ToInt64(await command.ExecuteScalarAsync());}
        static async Task Wait(Func<Task<bool>> condition,string message){for(var n=0;n<400;n++){if(await condition())return;await Task.Delay(100);}throw new Exception(message);}
    }
}
