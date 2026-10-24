using System.Globalization;
using System.Text.RegularExpressions;
using Meridian.Infrastructure.Features.PdfImports.Contracts;
using Npgsql;

namespace Meridian.Infrastructure.Features.PdfImports.Processing;

public sealed class PdfDraftValidator
{
    public static PdfDraft Normalize(PdfDraft draft, PdfDraft? original = null)
    {
        var sourceRows = (original?.Rows ?? []).GroupBy(r => r.RowNumber).ToDictionary(g => g.Key, g => g.First());
        return draft with
        {
            VoteId = (draft.VoteId ?? "").Trim(),
            Session = (draft.Session ?? "").Trim(),
            VoteDate = (draft.VoteDate ?? "").Trim(),
            Title = (draft.Title ?? "").Trim(),
            PublishedAt = (draft.PublishedAt ?? "").Trim(),
            Rows = (draft.Rows ?? []).Take(2001).Select(row =>
            {
                sourceRows.TryGetValue(row.RowNumber, out var source);
                return row with
                {
                    CountryCode = (row.CountryCode ?? "").Trim().ToUpperInvariant(),
                    CountryName = (row.CountryName ?? "").Trim(),
                    Ballot = (row.Ballot ?? "").Trim().ToUpperInvariant(),
                    PageNumber = source?.PageNumber ?? 0,
                    Confidence = source?.Confidence,
                    SourceText = source?.SourceText ?? "Manually entered during review"
                };
            }).ToArray()
        };
    }

    public async Task<IReadOnlyList<PdfIssue>> ValidateAsync(PdfDraft draft, NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var issues = new List<PdfIssue>();
        void Error(string code, string message, int? row = null) => issues.Add(new(code, "Error", message, row));
        void Warning(string code, string message, int? row = null) => issues.Add(new(code, "Warning", message, row));
        if (draft.VoteId.Length is < 1 or > 100)
            Error("voteId", "Enter the vote ID printed on the sheet.");
        if (!int.TryParse(draft.Session, out var session) || session is < 1 or > 9999)
            Error("session", "Enter a valid positive session.");
        var validDate = DateOnly.TryParseExact(draft.VoteDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date);
        if (!validDate)
            Error("voteDate", "Enter the vote date as yyyy-mm-dd.");
        if (draft.Title.Length is < 1 or > 2000)
            Error("title", "Enter a resolution title (up to 2,000 characters).");
        if (draft.PublishedAt.Length > 100 || !Regex.IsMatch(draft.PublishedAt, @"T.*(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.None, TimeSpan.FromMilliseconds(100)) || !DateTimeOffset.TryParse(draft.PublishedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            Error("publishedAt", "Enter the source publication timestamp with a time zone. It is not inferred from the upload time.");
        if (draft.Rows.Count is < 1 or > 2000)
            Error("rows", "The draft must contain 1–2,000 country rows.");
        var periods = new Dictionary<string, List<(DateOnly From, DateOnly? To)>>();
        await using (var command = new NpgsqlCommand("SELECT country_code,member_from,member_to FROM membership_periods", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var code = reader.GetString(0).Trim();
                if (!periods.TryGetValue(code, out var list))
                    periods[code] = list = [];
                list.Add((reader.GetFieldValue<DateOnly>(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateOnly>(2)));
            }

        var duplicateCodes = draft.Rows.GroupBy(r => r.CountryCode).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var duplicateRows = draft.Rows.GroupBy(r => r.RowNumber).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var row in draft.Rows.Take(2000))
        {
            if (row.RowNumber < 1 || duplicateRows.Contains(row.RowNumber))
                Error("rowNumber", "Each row must have a unique positive row number.", row.RowNumber);
            if (!periods.TryGetValue(row.CountryCode, out var memberships))
                Error("countryCode", "Unknown country code. Correct it using the source PDF.", row.RowNumber);
            else if (validDate && !memberships.Any(p => p.From <= date && (!p.To.HasValue || date <= p.To.Value)))
                Warning("membership", "This country was not a member on the vote date; the CSV validator will reject this row.", row.RowNumber);
            if (duplicateCodes.Contains(row.CountryCode))
                Error("duplicateCountry", "Country appears more than once in this sheet.", row.RowNumber);
            if (row.CountryName.Length is < 1 or > 300)
                Error("countryName", "Enter the country name shown on the sheet.", row.RowNumber);
            if (row.Ballot is not ("Y" or "N" or "A" or "X"))
                Error("ballot", "Ambiguous marks: select Y, N, A or X after checking the source.", row.RowNumber);
            if (row.Confidence is null || row.Confidence < 0.85 || row.Confidence > 1)
                Warning("confidence", row.Ballot == "X" ? "Blank voting cells were interpreted as absence. Verify this row." : "Low or unavailable OCR confidence. Verify this row.", row.RowNumber);
        }

        if (draft.PrintedTotals is not { } totals)
            Error("printedTotals", "Enter all four printed totals from the PDF.");
        else
        {
            if (totals.Yes < 0 || totals.No < 0 || totals.Abstain < 0 || totals.Absent < 0)
                Error("printedTotals", "Printed totals cannot be negative.");
            if (totals.Yes != draft.Rows.Count(r => r.Ballot == "Y") || totals.No != draft.Rows.Count(r => r.Ballot == "N") || totals.Abstain != draft.Rows.Count(r => r.Ballot == "A") || totals.Absent != draft.Rows.Count(r => r.Ballot == "X"))
                Error("totalsMismatch", "Country ballots do not match the four printed totals. Correct the marks or totals before approval.");
        }

        Warning("metadataReview", "Verify all vote details and printed totals against the original PDF; OCR is not authoritative.");
        return issues;
    }
}
