using Meridian.Infrastructure;

var path = args.FirstOrDefault() ?? throw new ArgumentException("Pass the path to assembly_export_2024_q1.csv.");
using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
var count = 0;
var firstVoteTitle = "";
var malformedRows = 0;
await foreach (var row in CsvRecordReader.ReadAsync(reader))
{
    if (count == 0)
    {
        if (row.Count != 8 || row[0] != "assembly_vote_id") throw new InvalidDataException("Unexpected CSV header.");
    }
    else
    {
        if (row.Count != 8)
        {
            malformedRows++;
            if (count + 1 != 3139 || row.Count != 5)
                throw new InvalidDataException($"Unexpected malformed CSV record {count + 1}: {row.Count} fields.");
            count++;
            continue;
        }
        if (count == 1) firstVoteTitle = row[3];
    }
    count++;
}
if (count < 5500 || count > 7000) throw new InvalidDataException($"Unexpected Q1 record count: {count - 1}.");
if (malformedRows != 1) throw new InvalidDataException($"Expected the supplied malformed row to be preserved, but found {malformedRows} malformed records.");
if (!firstVoteTitle.Contains(',')) throw new InvalidDataException("Quoted comma in resolution_title was not preserved.");
Console.WriteLine($"PASS: parsed {count - 1} Q1 records, preserved the malformed 5-field row, resumed parsing, and retained quoted title fields.");
