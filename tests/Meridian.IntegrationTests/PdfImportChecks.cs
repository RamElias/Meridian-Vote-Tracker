using System.Net;
using System.Text;
using System.Text.Json;
using Meridian.Infrastructure.BackgroundJobs;
using Meridian.Infrastructure.Features.Imports.Processing;
using Meridian.Infrastructure.Features.PdfImports.Contracts;
using Meridian.Infrastructure.Features.PdfImports.Processing;
using Meridian.Infrastructure.Features.PdfImports.Services;
using Meridian.Infrastructure.Persistence;
using Meridian.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

internal static class PdfImportChecks
{
    public static async Task RunAsync(NpgsqlDataSource source, DatabaseBootstrapper bootstrap, IImportFileStorage storage, CsvImportJobProcessor csvWorker)
    {
        var fake = new FakeAnalysis();
        var validator = new PdfDraftValidator();
        var service = new PdfImportService(source, storage, fake, validator);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "DocumentIntelligence:TimeoutSeconds", "30" } }).Build();
        var worker = new PdfExtractionJobProcessor(source, storage, fake, new PdfLayoutParser(), validator, new JobProgressReporter(source), config, NullLogger<PdfExtractionJobProcessor>.Instance);
        var upload = await Upload("first");
        Check(await Count("SELECT count(*) FROM votes WHERE assembly_vote_id='MA/2024/777'") == 0, "PDF upload creates no approved vote");
        await worker.ProcessNextAsync(default);
        var item = (await service.GetAsync(upload.Id, default))!;
        Check(item.Status == "AwaitingReview" && item.Draft?.Rows.Count == 2, "PDF layout parses both tables into a persistent draft");
        Check(item.Draft!.Rows[1].Ballot == "X" && item.Issues.Any(i => i.Code == "confidence"), "blank PDF cells and low confidence require source review");
        Check(item.Issues.Any(i => i.Code == "publishedAt"), "PDF publication timestamp must be supplied by reviewer");
        Check(await Count("SELECT count(*) FROM votes WHERE assembly_vote_id='MA/2024/777'") == 0, "extraction and validation create no approved vote");
        var draft = item.Draft with
        {
            PublishedAt = "2024-01-02T00:00:00Z"
        };
        var result = await service.ReviewAsync(item.Id, new(item.Version, draft, null), true, "dana", "pdf-test", default);
        Check(!result.Applied, "PDF approval requires explicit warning acknowledgement");
        var mismatch = await service.ReviewAsync(item.Id, new(item.Version, draft with { PrintedTotals = new(0, 0, 0, 0) }, null, true), true, "dana", "pdf-test", default);
        Check(mismatch.Issues!.Any(i => i.Code == "totalsMismatch"), "PDF printed totals mismatch blocks approval");
        var duplicate = draft with
        {
            Rows = [draft.Rows[0], draft.Rows[1] with
            {
                CountryCode = "AAA"
            }

            ]
        };
        result = await service.ReviewAsync(item.Id, new(item.Version, duplicate, null, true), true, "dana", "pdf-test", default);
        Check(result.Issues!.Any(i => i.Code == "duplicateCountry"), "PDF duplicate country detected before approval");
        var invalid = draft with
        {
            Rows = [draft.Rows[0] with
            {
                CountryCode = "ZZZ",
                Ballot = "YN"
            }, draft.Rows[1]]
        };
        result = await service.ReviewAsync(item.Id, new(item.Version, invalid, null, true), true, "dana", "pdf-test", default);
        Check(result.Issues!.Any(i => i.Code == "countryCode") && result.Issues!.Any(i => i.Code == "ballot"), "PDF invalid country and non-exact ballot blocked");
        var saved = await service.ReviewAsync(item.Id, new(item.Version, draft, "Verified metadata"), false, "dana", "pdf-test", default);
        Check(saved.Applied && saved.Current!.Status == "AwaitingReview", "saving PDF draft retains unapproved state");
        result = await service.ReviewAsync(item.Id, new(item.Version, draft, null, true), true, "omer", "pdf-test", default);
        Check(result.Stale, "stale PDF draft cannot overwrite another reviewer's edits");
        item = saved.Current!;
        var approvals = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => service.ReviewAsync(item.Id, new(item.Version, draft, "Source checked", true), true, "dana", "pdf-test", default)));
        Check(approvals.Count(a => a.Applied) == 1 && approvals.Count(a => a.Stale) == 1, "concurrent PDF decisions: first approval wins");
        item = (await service.GetAsync(item.Id, default))!;
        Check(item.ImportId.HasValue && await Count($"SELECT count(*) FROM imports WHERE id='{item.ImportId}' AND source_pdf_extraction_id='{item.Id}'") == 1, "PDF approval atomically links one normal import");
        Check(await Count("SELECT count(*) FROM votes WHERE assembly_vote_id='MA/2024/777'") == 0, "approved PDF still waits for CSV domain validation");
        await csvWorker.ProcessNextAsync(default);
        Check(await Count("SELECT count(*) FROM country_votes WHERE assembly_vote_id='MA/2024/777'") == 2, "reviewed PDF uses normal CSV promotion");
        var repeated = await Upload("first");
        Check(repeated.Status == "Duplicate" && repeated.OriginalExtractionId == item.Id && fake.Starts == 1, "identical PDF records history without a second AI call");
        await using (var original = await service.OpenSourceAsync(item.Id, default))
        {
            using var reader = new StreamReader(original!);
            Check((await reader.ReadToEndAsync()).StartsWith("%PDF-"), "PDF original source retained");
        }

        fake.Yes = false;
        var correction = await Upload("correction");
        await worker.ProcessNextAsync(default);
        var corrected = (await service.GetAsync(correction.Id, default))!;
        var correctedDraft = corrected.Draft!with
        {
            PublishedAt = "2024-02-01T00:00:00Z"
        };
        result = await service.ReviewAsync(corrected.Id, new(corrected.Version, correctedDraft, null, true), true, "omer", "pdf-test", default);
        Check(result.Applied, "corrected PDF accepted for validation");
        await csvWorker.ProcessNextAsync(default);
        Check(await Count("SELECT count(*) FROM conflicts WHERE assembly_vote_id='MA/2024/777' AND country_code='AAA' AND status='Pending'") == 1, "PDF correction opens normal country conflict");
        var rejected = await Upload("rejected");
        await worker.ProcessNextAsync(default);
        var reject = (await service.GetAsync(rejected.Id, default))!;
        Check((await service.RejectAsync(reject.Id, new(reject.Version, "Discarded"), "dana", "pdf-test", default)).Applied && (await service.GetAsync(reject.Id, default))!.ImportId is null, "rejected PDF never queues an import");
        fake.FailOnce = true;
        var failure = await Upload("failure");
        await worker.ProcessNextAsync(default);
        var failed = (await service.GetAsync(failure.Id, default))!;
        Check(failed.Status == "Failed" && failed.Error!.Contains("pdf-test") && !failed.Error.Contains("sensitive-provider-detail"), "PDF service failure is safe, traceable and terminal");
        Check((await service.RetryAsync(failed.Id, new(failed.Version, null), "dana", "pdf-test", default)).Applied, "failed PDF can be explicitly retried");
        await worker.ProcessNextAsync(default);
        Check((await service.GetAsync(failed.Id, default))!.Status == "AwaitingReview", "PDF retry completes");
        config["DocumentIntelligence:TimeoutSeconds"] = "1";
        fake.Pending = true;
        var timeout = await Upload("timeout");
        await worker.ProcessNextAsync(default);
        Check((await service.GetAsync(timeout.Id, default))!.Status == "Failed", "PDF extraction deadline prevents permanently running jobs");
        fake.Pending = false;
        config["DocumentIntelligence:TimeoutSeconds"] = "30";
        var interrupted = await Upload("interrupted");
        await using (var simulate = source.CreateCommand("UPDATE background_jobs SET status='Running' WHERE id=@id; UPDATE pdf_extractions SET status='Running',operation_url=@url WHERE id=@id"))
        {
            simulate.Parameters.AddWithValue("id", interrupted.Id);
            simulate.Parameters.AddWithValue("url", FakeAnalysis.Operation);
            await simulate.ExecuteNonQueryAsync();
        }

        var starts = fake.Starts;
        await bootstrap.RecoverInterruptedJobsAsync(default);
        await worker.ProcessNextAsync(default);
        Check(fake.Starts == starts && (await service.GetAsync(interrupted.Id, default))!.Status == "AwaitingReview", "PDF recovery resumes persisted Azure operation without resubmission");
        await AssertImmutable();
        await CheckTransport();
        Console.WriteLine("PASS: PDF bonus workflow with simulated AI output, real PostgreSQL, review concurrency, normal import reuse, retry and recovery.");
        async Task<PdfExtraction> Upload(string marker)
        {
            using var input = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7\n" + marker));
            return await service.CreateAsync(marker + ".pdf", input, "dana", "pdf-test", default);
        }

        async Task<long> Count(string sql)
        {
            await using var command = source.CreateCommand(sql);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        async Task AssertImmutable()
        {
            try
            {
                await using var command = source.CreateCommand($"UPDATE pdf_extractions SET decision_note='tamper' WHERE id='{item.Id}'");
                await command.ExecuteNonQueryAsync();
                throw new Exception("Reviewed PDF was mutable");
            }
            catch (PostgresException ex)when (ex.SqlState == "55000")
            {
                Console.WriteLine("PASS: reviewed PDF immutable in PostgreSQL");
            }
        }
    }

    private static void Check(bool ok, string description)
    {
        if (!ok)
            throw new Exception(description);
        Console.WriteLine("PASS: " + description);
    }

    private static async Task CheckTransport()
    {
        var handler = new CaptureHandler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { { "DocumentIntelligence:Endpoint", "https://example.cognitiveservices.azure.com" }, { "DocumentIntelligence:ApiKey", "test-only-key" } }).Build();
        using var client = new AzureDocumentAnalysisClient(new HttpClient(handler), config);
        using var input = new MemoryStream(Encoding.ASCII.GetBytes("%PDF-1.7"));
        var op = await client.StartAsync(input, "pdf-wire", default);
        var poll = await client.PollAsync(op, "pdf-wire", default);
        Check(handler.Posted && handler.Polled && poll.Status == "succeeded", "Azure adapter sends PDF bytes, configured credential and correlation ID with pinned API version");
        try
        {
            await client.PollAsync("https://other.example/documentintelligence/results/1", "pdf-wire", default);
            throw new Exception("Cross-host operation accepted");
        }
        catch (InvalidDataException)
        {
            Console.WriteLine("PASS: Azure operation host is validated before sending credentials");
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public bool Posted, Polled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(request.Headers.GetValues("Ocp-Apim-Subscription-Key").Single() == "test-only-key" && request.Headers.GetValues("x-ms-client-request-id").Single() == "pdf-wire", "Azure request trace and authentication headers");
            if (request.Method == HttpMethod.Post)
            {
                Check(request.RequestUri!.Query.Contains("api-version=2024-11-30") && request.Content!.Headers.ContentType!.MediaType == "application/pdf" && (await request.Content.ReadAsStringAsync(ct)).StartsWith("%PDF-"), "Azure REST binary upload contract");
                Posted = true;
                var response = new HttpResponseMessage(HttpStatusCode.Accepted);
                response.Headers.Add("Operation-Location", FakeAnalysis.Operation);
                return response;
            }

            Polled = true;
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { status = "succeeded", analyzeResult = JsonSerializer.Deserialize<JsonElement>(Layout(true)) }))
            };
        }
    }

    private sealed class FakeAnalysis : IDocumentAnalysisClient
    {
        public const string Operation = "https://example.cognitiveservices.azure.com/documentintelligence/documentModels/prebuilt-layout/analyzeResults/1?api-version=2024-11-30";
        public bool IsConfigured => true;

        public int Starts;
        public bool Yes = true, FailOnce, Pending;
        public Task<string> StartAsync(Stream pdf, string correlationId, CancellationToken ct)
        {
            Starts++;
            return Task.FromResult(Operation);
        }

        public Task<DocumentAnalysisPoll> PollAsync(string operationUrl, string correlationId, CancellationToken ct)
        {
            if (FailOnce)
            {
                FailOnce = false;
                throw new HttpRequestException("sensitive-provider-detail");
            }

            return Task.FromResult(Pending ? new DocumentAnalysisPoll("running") : new DocumentAnalysisPoll("succeeded", Layout(Yes)));
        }
    }

    private static string Layout(bool yes)
    {
        object Table(string code, string name, string y, string n, int page) => new
        {
            cells = new[]
            {
                Cell(0, 0, "Code", page),
                Cell(0, 1, "Member state", page),
                Cell(0, 2, "YES", page),
                Cell(0, 3, "NO", page),
                Cell(0, 4, "ABST.", page),
                Cell(1, 0, code, page),
                Cell(1, 1, name, page),
                Cell(1, 2, y, page),
                Cell(1, 3, n, page),
                Cell(1, 4, "", page)
            }
        };
        return JsonSerializer.Serialize(new { pages = new[] { new { lines = new[] { "Vote No.: MA/2024/777", "Session: 79", "Date of vote: 1 January 2024", "Resolution: PDF regression", $"Recorded totals: Yes: {(yes ? 1 : 0)} No: {(yes ? 0 : 1)} Abstentions: 0 Not voting: 1" }.Select(content => new { content }), words = new[] { new { content = "AAA", confidence = 0.4, span = new { offset = 0, length = 3 } } } } }, tables = new[] { Table("AAA", "Alpha", yes ? "X" : "", yes ? "" : "X", 1), Table("BBB", "Beta", "", "", 2) } });
    }

    private static object Cell(int row, int col, string content, int page) => new
    {
        rowIndex = row,
        columnIndex = col,
        content,
        boundingRegions = new[]
        {
            new
            {
                pageNumber = page
            }
        },
        spans = new[]
        {
            new
            {
                offset = 0,
                length = 3
            }
        }
    };
}
