using System.Globalization;
namespace Meridian.Infrastructure.Features.Imports.Processing;
internal static class CsvValueNormalizer
{
    public static (Dictionary<string, object?> Values, Dictionary<string, object> Changes) Normalize(Dictionary<string, object?> raw)
    {
        var values = new Dictionary<string, object?>(raw, StringComparer.OrdinalIgnoreCase);
        var changes = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            if (value is not string original) continue;
            var normalized = original.Trim();
            if (key == "session" && int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var session)) normalized = session.ToString(CultureInfo.InvariantCulture);
            values[key] = normalized;
            if (normalized != original) changes[key] = new { original, normalized };
        }
        return (values, changes);
    }
}
