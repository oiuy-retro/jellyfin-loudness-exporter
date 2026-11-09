using System.Globalization;
using System.Text.Json;

namespace JellyfinLoudnessExporter;

internal static class GainParser
{
    public enum ResultKind
    {
        Missing,
        Finite,
        NonFinite,
        InvalidType
    }

    public readonly record struct ParseResult(ResultKind Kind, double? Value);

    public static ParseResult Parse(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null || value.ValueKind == JsonValueKind.Undefined)
        {
            return new ParseResult(ResultKind.Missing, null);
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            // Reject non-finite if represented (STJ typically won't emit NaN in strict JSON).
            if (!value.TryGetDouble(out var d) || double.IsNaN(d) || double.IsInfinity(d))
            {
                return new ParseResult(ResultKind.NonFinite, null);
            }

            return new ParseResult(ResultKind.Finite, d);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var s = value.GetString();
            if (string.IsNullOrEmpty(s))
            {
                return new ParseResult(ResultKind.InvalidType, null);
            }

            if (string.Equals(s, "NaN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "Infinity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "-Infinity", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s, "+Infinity", StringComparison.OrdinalIgnoreCase))
            {
                return new ParseResult(ResultKind.NonFinite, null);
            }

            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                // Numeric strings are invalid types for Jellyfin DTO gains (must be JSON number).
                return new ParseResult(ResultKind.InvalidType, null);
            }

            return new ParseResult(ResultKind.InvalidType, null);
        }

        // Boolean / object / array
        return new ParseResult(ResultKind.InvalidType, null);
    }

    public static bool IsOutOfRange(double v) => Math.Abs(v) > ExportBuilder.OutOfRangeAbsDb;
}
