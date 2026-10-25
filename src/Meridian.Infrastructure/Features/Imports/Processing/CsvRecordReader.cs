using System.Collections;
using System.Runtime.CompilerServices;
using System.Text;

namespace Meridian.Infrastructure.Features.Imports.Processing;

public sealed record CsvRecord(IReadOnlyList<string> Fields, string Source, string? Error) : IReadOnlyList<string>
{
    public int Count => Fields.Count;
    public string this[int index] => Fields[index];
    public IEnumerator<string> GetEnumerator() => Fields.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public static class CsvRecordReader
{
    private enum State { Start, Unquoted, Quoted, Closed }

    // A malformed unquoted/closed field recovers at its next record boundary.
    // An unterminated quoted suffix is ONE invalid record; newlines within it
    // cannot safely be assumed to start new records. Valid prior records survive.
    public static async IAsyncEnumerable<CsvRecord> ReadAsync(
        TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new char[64 * 1024];
        var fields = new List<string>();
        var field = new StringBuilder();
        var source = new StringBuilder();
        var state = State.Start;
        string? error = null;
        var skipLf = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ch = buffer[index];
                if (skipLf) { skipLf = false; if (ch == '\n') continue; }
                if (state != State.Quoted && (ch == '\r' || ch == '\n'))
                {
                    fields.Add(field.ToString());
                    yield return new CsvRecord(fields, source.ToString(), error);
                    fields = []; field.Clear(); source.Clear(); error = null; state = State.Start;
                    skipLf = ch == '\r';
                    continue;
                }
                source.Append(ch);
                if (state == State.Quoted)
                {
                    if (ch == '"') state = State.Closed;
                    else field.Append(ch);
                }
                else if (ch == ',' )
                {
                    fields.Add(field.ToString()); field.Clear(); state = State.Start;
                }
                else if (ch == '"')
                {
                    if (state == State.Start) state = State.Quoted;
                    else if (state == State.Closed) { field.Append('"'); state = State.Quoted; }
                    else { error ??= "Unexpected quote inside an unquoted CSV field."; field.Append(ch); }
                }
                else
                {
                    if (state == State.Closed) error ??= "Unexpected character after a closing CSV quote.";
                    field.Append(ch); state = State.Unquoted;
                }
            }
        }
        if (source.Length > 0 || fields.Count > 0)
        {
            if (state == State.Quoted) error ??= "CSV ended inside a quoted field; remaining text retained as one invalid record.";
            fields.Add(field.ToString());
            yield return new CsvRecord(fields, source.ToString(), error);
        }
    }
}
