using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Npgsql;

internal static class HttpApiChecks
{
    public static async Task RunAsync(string connectionString, NpgsqlDataSource source, Guid importId, Guid snapshotId)
    {
        var apiDll = Environment.GetEnvironmentVariable("MERIDIAN_TEST_API_DLL") ?? Path.GetFullPath("src/Meridian.Api/bin/Debug/net8.0/Meridian.Api.dll");
        if (!File.Exists(apiDll)) throw new InvalidOperationException("Build Meridian.VoteTracker.sln before running integration tests (or set MERIDIAN_TEST_API_DLL).");
        var listener = new TcpListener(IPAddress.Loopback,0); listener.Start(); var port=((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var start=new ProcessStartInfo("dotnet",apiDll) { RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false };
        start.Environment["ASPNETCORE_URLS"]=$"http://127.0.0.1:{port}";
        start.Environment["ASPNETCORE_ENVIRONMENT"]="Production";
        start.Environment["ConnectionStrings__Meridian"]=connectionString;
        start.Environment["Azure__KeyVault__VaultUri"]="";
        start.Environment["Seed__MemberStatesPath"]="";
        var logs=new StringBuilder();
        using var process=Process.Start(start) ?? throw new Exception("Could not start API");
        process.OutputDataReceived+=(_,e)=> { lock(logs) logs.AppendLine(e.Data); };
        process.ErrorDataReceived+=(_,e)=> { lock(logs) logs.AppendLine(e.Data); };
        process.BeginOutputReadLine();process.BeginErrorReadLine();
        using var client=new HttpClient { BaseAddress=new Uri($"http://127.0.0.1:{port}"),Timeout=TimeSpan.FromSeconds(10) };
        try
        {
            var ready=false;
            for(var n=0;n<200;n++)
            {
                if(process.HasExited) throw new Exception("API exited: "+logs);
                try { using var health=await client.GetAsync("/health"); if(health.IsSuccessStatusCode){ready=true;break;} } catch(HttpRequestException) { }
                await Task.Delay(100);
            }
            if(!ready) throw new Exception("API did not become ready: "+logs);
            using var openApi=JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
            var contract=openApi.RootElement;
            using (var metadata = await client.GetAsync("/api/v1/meta"))
                if (!metadata.IsSuccessStatusCode) throw new Exception("Public metadata requires authentication.");
            await Request(HttpMethod.Get,"/api/v1/votes",null,null,401,"Problem");
            await Request(HttpMethod.Get,"/api/v1/vote","lana",null,400,"Problem");
            await Request(HttpMethod.Get,"/api/v1/votes?session=not-an-integer","lana",null,400,"Problem");
            await Request(HttpMethod.Put,"/api/v1/vote/position?voteId=m1","lana",new {stance="Y",weight=1},400,"Problem");
            await Request(HttpMethod.Get,"/api/v1/votes","unknown",null,401,"Problem");
            await Request(HttpMethod.Put,"/api/v1/vote/position?voteId=m1","dana",new {stance="Y",weight=1,expectedVersionId=(long?)null},403,"Problem");
            await Request(HttpMethod.Post,$"/api/v1/snapshots/{snapshotId}/publish","dana",new {year=2024,quarter=1,replaceActive=false},403,"Problem");
            await Request(HttpMethod.Get,"/api/v1/imports","lana",null,200,"ImportRecord",true);
            await Request(HttpMethod.Get,$"/api/v1/imports/{importId}","lana",null,200,"ImportDetails");
            await Request(HttpMethod.Get,$"/api/v1/imports/{importId}/rows?limit=100","lana",null,200,"ImportRow",true);
            await Request(HttpMethod.Get,"/api/v1/votes","lana",null,200,"VoteSummary",true);
            await Request(HttpMethod.Get,"/api/v1/vote?voteId=v1","lana",null,200,"VoteDetails");
            await Request(HttpMethod.Get,"/api/v1/conflicts?status=All","lana",null,200,"Conflict",true);
            await Request(HttpMethod.Get,"/api/v1/snapshots","lana",null,200,"Snapshot",true);
            await Request(HttpMethod.Get,$"/api/v1/snapshots/{snapshotId}","lana",null,200,"Snapshot");
            await Request(HttpMethod.Get,$"/api/v1/snapshots/{snapshotId}/results","lana",null,200,"SnapshotResult",true);
            await Request(HttpMethod.Get,$"/api/v1/snapshots/{snapshotId}/countries/AAA/contributions","lana",null,200,"VoteContribution",true);
            await Request(HttpMethod.Post,"/api/v1/snapshots","lana",new {voteIds=new[]{"m1","unknown-vote"}},422,"Problem");
            await using var version=source.CreateCommand("SELECT current_version_id FROM vote_positions WHERE assembly_vote_id='m1'");
            var current=(long)(await version.ExecuteScalarAsync())!;
            await Request(HttpMethod.Put,"/api/v1/vote/position?voteId=m1","lana",new {stance="Y",weight=2,expectedVersionId=current},200,"PositionResult");
            var stale=await Request(HttpMethod.Put,"/api/v1/vote/position?voteId=m1","lana",new {stance="N",weight=2,expectedVersionId=current},409,"Problem");
            if(!stale.TryGetProperty("currentPosition",out _)) throw new Exception("409 does not include current position");
            // Known user input errors are detailed; internal failures are safe and traceable.
            using (var upload=new HttpRequestMessage(HttpMethod.Post,"/api/v1/imports"))
            {
                upload.Headers.Add("X-Demo-User","dana");
                using var form=new MultipartFormDataContent();form.Add(new StringContent("not csv"),"file","file.txt");upload.Content=form;
                using var response=await client.SendAsync(upload);
                if((int)response.StatusCode!=400 || !(await response.Content.ReadAsStringAsync()).Contains(".csv")) throw new Exception("Known invalid upload was not explained");
            }
            // A temporary table rename makes list-votes fail without changing its data.
            await using(var rename=source.CreateCommand("ALTER TABLE vote_positions RENAME TO positions_during_failure")) await rename.ExecuteNonQueryAsync();
            try
            {
                var failure=await Request(HttpMethod.Get,"/api/v1/votes","lana",null,500,"Problem");
                if(failure.GetProperty("detail").GetString()!.Contains("positions_during_failure") || !failure.TryGetProperty("correlationId",out _)) throw new Exception("Internal error leaks database details or lacks correlation ID");
            }
            finally { await using var restore=source.CreateCommand("ALTER TABLE positions_during_failure RENAME TO vote_positions");await restore.ExecuteNonQueryAsync(); }
            Console.WriteLine("PASS: live HTTP API roles, optimistic concurrency, safe errors and OpenAPI response contracts.");

            async Task<JsonElement> Request(HttpMethod method,string path,string? user,object? body,int expected,string schema,bool array=false)
            {
                using var request=new HttpRequestMessage(method,path);
                request.Headers.Add("X-Correlation-ID","http-regression");
                if(user is not null) request.Headers.Add("X-Demo-User",user);
                if(body is not null) request.Content=JsonContent.Create(body);
                using var response=await client.SendAsync(request);
                var text=await response.Content.ReadAsStringAsync();
                if((int)response.StatusCode!=expected) throw new Exception($"{method} {path}: expected {expected}, got {(int)response.StatusCode}: {text}");
                if(!response.Headers.TryGetValues("X-Correlation-ID",out var ids) || ids.Single()!="http-regression") throw new Exception("Missing correlation response header");
                using var document=JsonDocument.Parse(text);
                var value=document.RootElement;
                if(array) foreach(var item in value.EnumerateArray()) Validate(item,contract.GetProperty("components").GetProperty("schemas").GetProperty(schema),contract,schema);
                else Validate(value,contract.GetProperty("components").GetProperty("schemas").GetProperty(schema),contract,schema);
                return value.Clone();
            }
        }
        finally { if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();} }
    }

    // Enforces the object/array/required/enum/nullability subset used by this contract
    // against actual HTTP responses. The generated-client drift check is separate.
    private static void Validate(JsonElement value,JsonElement schema,JsonElement contract,string location)
    {
        if(value.ValueKind==JsonValueKind.Null && schema.TryGetProperty("nullable",out var nullable) && nullable.GetBoolean()) return;
        if(schema.TryGetProperty("$ref",out var reference)) { Validate(value,contract.GetProperty("components").GetProperty("schemas").GetProperty(reference.GetString()!.Split('/').Last()),contract,location);return; }
        if(schema.TryGetProperty("allOf",out var all)) foreach(var child in all.EnumerateArray()) Validate(value,child,contract,location);
        if(schema.TryGetProperty("type",out var type))
        {
            var matches=type.GetString() switch { "object"=>value.ValueKind==JsonValueKind.Object,"array"=>value.ValueKind==JsonValueKind.Array,"string"=>value.ValueKind==JsonValueKind.String,"integer"=>value.ValueKind==JsonValueKind.Number && value.TryGetDecimal(out var number) && decimal.Truncate(number)==number,"number"=>value.ValueKind==JsonValueKind.Number,"boolean"=>value.ValueKind is JsonValueKind.True or JsonValueKind.False,_=>true };
            if(!matches) throw new Exception($"Contract {location}: expected {type}, got {value.ValueKind}");
        }
        if(schema.TryGetProperty("enum",out var options) && !options.EnumerateArray().Any(option=>option.GetRawText()==value.GetRawText())) throw new Exception("Contract enum mismatch: "+location);
        if(value.ValueKind==JsonValueKind.Object)
        {
            if(schema.TryGetProperty("required",out var required)) foreach(var field in required.EnumerateArray()) if(!value.TryGetProperty(field.GetString()!,out _)) throw new Exception($"Contract {location}: missing {field.GetString()}");
            if(schema.TryGetProperty("properties",out var properties)) foreach(var field in properties.EnumerateObject()) if(value.TryGetProperty(field.Name,out var actual)) Validate(actual,field.Value,contract,location+"."+field.Name);
        }
        if(value.ValueKind==JsonValueKind.Array && schema.TryGetProperty("items",out var items)) foreach(var child in value.EnumerateArray()) Validate(child,items,contract,location+"[]");
    }
}
