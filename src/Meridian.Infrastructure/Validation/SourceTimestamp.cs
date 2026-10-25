using System.Globalization;
using System.Text.RegularExpressions;

namespace Meridian.Infrastructure.Validation;

public static class SourceTimestamp
{
    public static bool IsValid(string value) =>
        Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.None, TimeSpan.FromMilliseconds(100))
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
}
