using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meridian.Infrastructure.Features.PdfImports.Contracts;

namespace Meridian.Infrastructure.Features.PdfImports.Processing;
// Deterministic adapter for the supplied Record of Vote layout. Never infers a code from a country name.
public sealed class PdfLayoutParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private static string Match(string text, string pattern)
    {
        var values = Regex.Matches(text, pattern, RegexOptions.IgnoreCase, RegexTimeout).Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return values.Length == 1 ? values[0] : "";
    }

    public PdfDraft Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength()is < 1 or > 20)
            throw new InvalidDataException("Expected an analysis of 1–20 PDF pages.");
        var lines = new List<string>();
        var confidence = new List<(int Offset, int Length, double Confidence)>();
        foreach (var page in pages.EnumerateArray())
        {
            if (page.TryGetProperty("lines", out var pageLines))
                lines.AddRange(pageLines.EnumerateArray().Select(line => line.GetProperty("content").GetString() ?? ""));
            foreach (var property in new[]
            {
                "words",
                "selectionMarks"
            }

            )
                if (page.TryGetProperty(property, out var words))
                    foreach (var word in words.EnumerateArray())
                        if (word.TryGetProperty("span", out var span) && word.TryGetProperty("confidence", out var value))
                            confidence.Add((span.GetProperty("offset").GetInt32(), span.GetProperty("length").GetInt32(), value.GetDouble()));
        }

        var text = string.Join('\n', lines);
        if (text.Length == 0 && root.TryGetProperty("content", out var content))
            text = content.GetString() ?? "";
        if (text.Length > 1_000_000)
            throw new InvalidDataException("The extracted document text is too large.");
        var voteIds = Regex.Matches(text, @"MA\s*/\s*\d{4}\s*/\s*\d+", RegexOptions.IgnoreCase, RegexTimeout).Select(m => Regex.Replace(m.Value.ToUpperInvariant(), @"\s", "", RegexOptions.None, RegexTimeout)).Distinct().ToArray();
        var voteId = voteIds.Length == 1 ? voteIds[0] : "";
        var session = Match(text, @"Session\s*:\s*(\d+)");
        var dateText = Match(text, @"Date\s+of\s+vote\s*:\s*([^\r\n]+)");
        var date = DateOnly.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate) ? parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        var title = Match(text, @"Resolution\s*:\s*([^\r\n]+)");
        var totalMatch = Regex.Match(text, @"Recorded\s+totals\s*:\s*Yes\s*:\s*(\d+)\s+No\s*:\s*(\d+)\s+Abstentions\s*:\s*(\d+)\s+Not\s+voting\s*:\s*(\d+)", RegexOptions.IgnoreCase, RegexTimeout);
        PdfTotals? totals = totalMatch.Success && Enumerable.Range(1, 4).All(i => int.TryParse(totalMatch.Groups[i].Value, out _)) ? new(int.Parse(totalMatch.Groups[1].Value), int.Parse(totalMatch.Groups[2].Value), int.Parse(totalMatch.Groups[3].Value), int.Parse(totalMatch.Groups[4].Value)) : null;
        var rows = new List<PdfDraftRow>();
        if (root.TryGetProperty("tables", out var tables))
            foreach (var table in tables.EnumerateArray())
            {
                if (!table.TryGetProperty("cells", out var rawCells) || rawCells.GetArrayLength() > 20_000)
                    throw new InvalidDataException("The extracted table structure is unsupported.");
                var cells = rawCells.EnumerateArray().ToArray();
                var headers = cells.Where(c => Clean(c) == "CODE").ToArray();
                foreach (var header in headers)
                {
                    var headerRow = header.GetProperty("rowIndex").GetInt32();
                    var codeColumn = header.GetProperty("columnIndex").GetInt32();
                    var columns = new Dictionary<string, int>();
                    foreach (var cell in cells.Where(c => c.GetProperty("rowIndex").GetInt32() == headerRow && c.GetProperty("columnIndex").GetInt32() > codeColumn && c.GetProperty("columnIndex").GetInt32() < codeColumn + 5))
                        columns[Clean(cell)] = cell.GetProperty("columnIndex").GetInt32();
                    if (!columns.ContainsKey("YES") || !columns.ContainsKey("NO") || !columns.Keys.Any(k => k.StartsWith("ABST", StringComparison.Ordinal)))
                        continue;
                    var abstColumn = columns.First(p => p.Key.StartsWith("ABST", StringComparison.Ordinal)).Value;
                    foreach (var rowGroup in cells.Where(c => c.GetProperty("rowIndex").GetInt32() > headerRow).GroupBy(c => c.GetProperty("rowIndex").GetInt32()).OrderBy(g => g.Key))
                    {
                        // A continued header begins a new segment; don't parse it twice.
                        if (headers.Any(h => h.GetProperty("rowIndex").GetInt32() > headerRow && h.GetProperty("rowIndex").GetInt32() <= rowGroup.Key && h.GetProperty("columnIndex").GetInt32() == codeColumn))
                            break;
                        var row = rowGroup.GroupBy(c => c.GetProperty("columnIndex").GetInt32()).ToDictionary(g => g.Key, g => g.First());
                        string Cell(int col) => row.TryGetValue(col, out var c) ? c.GetProperty("content").GetString()?.Trim() ?? "" : "";
                        var code = Cell(codeColumn);
                        var name = Cell(codeColumn + 1);
                        if (string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(name))
                            continue;
                        var marks = new[]
                        {
                            (columns["YES"], "Y"),
                            (columns["NO"], "N"),
                            (abstColumn, "A")
                        };
                        var selected = marks.Where(m => IsSelected(Cell(m.Item1))).ToArray();
                        var unknown = marks.Any(m => !IsSelected(Cell(m.Item1)) && !IsBlank(Cell(m.Item1)));
                        var ballot = !unknown && selected.Length == 1 ? selected[0].Item2 : !unknown && selected.Length == 0 ? "X" : "";
                        var relevant = row.Where(p => p.Key >= codeColumn && p.Key <= codeColumn + 4).Select(p => p.Value).ToArray();
                        var spans = relevant.Where(c => c.TryGetProperty("spans", out _)).SelectMany(c => c.GetProperty("spans").EnumerateArray()).ToArray();
                        var scores = confidence.Where(w => spans.Any(sp => w.Offset < sp.GetProperty("offset").GetInt32() + sp.GetProperty("length").GetInt32() && w.Offset + w.Length > sp.GetProperty("offset").GetInt32())).Select(w => w.Confidence).ToArray();
                        double? score = scores.Length == 0 ? null : scores.Min();
                        if (ballot == "X")
                            score = Math.Min(score ?? 0.5, 0.5); // Blank marks are review-sensitive absence, never high confidence.
                        var pageNumber = relevant.Where(c => c.TryGetProperty("boundingRegions", out _)).SelectMany(c => c.GetProperty("boundingRegions").EnumerateArray()).Select(b => b.GetProperty("pageNumber").GetInt32()).DefaultIfEmpty(1).First();
                        rows.Add(new(rows.Count + 1, pageNumber, code.ToUpperInvariant(), name, ballot, score, string.Join(" | ", Enumerable.Range(codeColumn, 5).Select(Cell))));
                        if (rows.Count > 2000)
                            throw new InvalidDataException("The extracted document contains too many rows.");
                    }
                }
            }

        // Publication time is absent from these scans and must be supplied explicitly by the reviewer.
        return new(voteId, session, date, title, "", totals, rows);
    }

    private static string Clean(JsonElement c) => (c.GetProperty("content").GetString() ?? "").Trim().ToUpperInvariant().TrimEnd('.');
    private static bool IsSelected(string value) => value.Trim().ToLowerInvariant()is "x" or "×" or "✕" or "✗" or "✓" or "✔" or "☒" or "☑" or ":selected:" or "selected";
    private static bool IsBlank(string value) => value.Trim().ToLowerInvariant()is "" or "☐" or ":unselected:" or "unselected";
}
