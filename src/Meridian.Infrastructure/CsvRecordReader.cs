using System.Runtime.CompilerServices;
using System.Text;

namespace Meridian.Infrastructure;

public static class CsvRecordReader
{
    public static async IAsyncEnumerable<IReadOnlyList<string>> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new char[64 * 1024];
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var quotePending = false;
        var skipLineFeed = false;
        var hasData = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ch = buffer[index];
                if (skipLineFeed)
                {
                    skipLineFeed = false;
                    if (ch == '\n') continue;
                }
                if (inQuotes && quotePending)
                {
                    if (ch == '"') { field.Append('"'); quotePending = false; hasData = true; continue; }
                    inQuotes = false;
                    quotePending = false;
                }
                if (inQuotes)
                {
                    if (ch == '"') quotePending = true;
                    else field.Append(ch);
                    hasData = true;
                    continue;
                }
                if (ch == '"') { inQuotes = true; hasData = true; }
                else if (ch == ',') { fields.Add(field.ToString()); field.Clear(); hasData = true; }
                else if (ch == '\r' || ch == '\n')
                {
                    fields.Add(field.ToString());
                    field.Clear();
                    yield return fields;
                    fields = new List<string>();
                    hasData = false;
                    if (ch == '\r') skipLineFeed = true;
                }
                else { field.Append(ch); hasData = true; }
            }
        }
        if (quotePending) { inQuotes = false; quotePending = false; }
        if (inQuotes) throw new InvalidDataException("CSV ended inside a quoted field.");
        if (hasData || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields;
        }
    }
}
