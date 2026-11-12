using System.Globalization;

namespace WideLong;

public enum ScaleMethod { Mean, Sum }

/// <summary>A scale score computed from numeric items of one file, after optional reverse coding:
/// reversed = (ReverseMin + ReverseMax) − value. A record gets a score only when at least MinItems items are present.
/// The score is a derived column added after the file's own columns; the original file is never changed.</summary>
public sealed class ScaleScore
{
    public string Name { get; set; } = "";
    public List<string> Items { get; set; } = [];
    public List<string> Reversed { get; set; } = [];
    public decimal ReverseMin { get; set; } = 1;
    public decimal ReverseMax { get; set; } = 5;
    public ScaleMethod Method { get; set; } = ScaleMethod.Mean;
    public int MinItems { get; set; } = 1;
    public ScaleScore Copy() => new() { Name = Name, Items = [.. Items], Reversed = [.. Reversed], ReverseMin = ReverseMin, ReverseMax = ReverseMax, Method = Method, MinItems = MinItems };
    public string Describe() => $"{Method.ToString().ToLowerInvariant()} of {Items.Count} items" + (Reversed.Count > 0 ? $" ({Reversed.Count} reverse-coded as {ReverseMin.ToString(CultureInfo.InvariantCulture)}+{ReverseMax.ToString(CultureInfo.InvariantCulture)}−x)" : "") + $", at least {MinItems} present";
}

public static class Scales
{
    /// <summary>Item columns, by their original header (stable when variables are renamed).</summary>
    public static List<int> ItemColumns(Source source, ScaleScore scale) => scale.Items.Select(item => source.Fields.FindIndex(f => f.Original == item)).ToList();

    /// <summary>Checks a scale definition against the file. Items must be distinct, numeric, non-key, non-repeated columns of the file itself.</summary>
    public static string? Problem(Source source, ScaleScore scale, ScaleScore? replacing = null)
    {
        if (scale.Name.Trim().Length == 0) return "Name the scale.";
        if (source.Fields.Any(f => f.Name.Equals(scale.Name, StringComparison.OrdinalIgnoreCase) && !(replacing != null && f.Original == replacing.Name))) return $"{source.Name} already has a variable named {scale.Name}.";
        if (scale.Items.Count < 2) return "Choose at least two items.";
        if (scale.Items.Distinct(StringComparer.Ordinal).Count() != scale.Items.Count) return "Each item can be used once.";
        foreach (var (item, column) in scale.Items.Zip(ItemColumns(source, scale)))
        {
            if (column < 0 || column >= source.FileColumns) return $"{item} is not a column of {source.Name}.";
            if (source.IsKeyColumn(column)) return $"{item} is a matching key and cannot be a scale item.";
            if (source.Fields[column].Measure.Length > 0) return $"{item} is a repeated column. Score repeated items in the long layout of a file that has one row per time.";
            if (source.Fields[column].Kind != ValueKind.Number) return $"{source.Fields[column].Name} is not a Number yet. Right-click it → Change variable type → Number first.";
        }
        if (scale.Reversed.Any(r => !scale.Items.Contains(r, StringComparer.Ordinal))) return "Reverse-coded items must be scale items.";
        if (scale.Reversed.Count > 0 && scale.ReverseMin >= scale.ReverseMax) return "For reverse coding, the lowest possible answer must be below the highest.";
        if (scale.MinItems < 1 || scale.MinItems > scale.Items.Count) return $"The minimum number of answered items must be between 1 and {scale.Items.Count}.";
        return null;
    }

    /// <summary>The score for one record: G15 text (matching what R and SAS compute in double precision), or "" when too few items are present.</summary>
    public static string Score(Source source, ScaleScore scale, string[] row, IReadOnlyList<int> columns)
    {
        var values = new List<double>();
        for (int i = 0; i < columns.Count; i++)
        {
            int c = columns[i]; string text = row[c];
            if (source.IsMissing(text, c) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) continue;
            if (scale.Reversed.Contains(scale.Items[i], StringComparer.Ordinal)) value = (double)(scale.ReverseMin + scale.ReverseMax) - value;
            values.Add(value);
        }
        if (values.Count < scale.MinItems || values.Count == 0) return "";
        double score = scale.Method == ScaleMethod.Sum ? values.Sum() : values.Average();
        return score.ToString("G15", CultureInfo.InvariantCulture);
    }

    /// <summary>Recomputes every scale column of these rows (any width; columns are added as needed).</summary>
    public static void Compute(Source source, List<string[]> rows)
    {
        int width = source.FileColumns + source.Scales.Count;
        for (int r = 0; r < rows.Count; r++)
            if (rows[r].Length != width) { var resized = new string[width]; Array.Copy(rows[r], resized, Math.Min(width, rows[r].Length)); for (int c = rows[r].Length; c < width; c++) resized[c] = ""; rows[r] = resized; }
        for (int i = 0; i < source.Scales.Count; i++)
        {
            var scale = source.Scales[i]; var columns = ItemColumns(source, scale); int target = source.FileColumns + i;
            bool valid = columns.All(c => c >= 0 && c < source.FileColumns);
            foreach (var row in rows) row[target] = valid ? Score(source, scale, row, columns) : "";
        }
    }
    public static void Refresh(Source source) { if (source.Scales.Count > 0 || source.Rows.Any(r => r.Length != source.Fields.Count)) Compute(source, source.Rows); }

    /// <summary>Adds (or replaces) a scale: a derived Number column after the file's own columns.</summary>
    public static void Add(Source source, ScaleScore scale, ScaleScore? replacing = null)
    {
        if (Problem(source, scale, replacing) is { } problem) throw new InvalidOperationException(problem);
        source.CaptureOriginal();
        int index = replacing == null ? -1 : source.Scales.IndexOf(replacing);
        if (index >= 0)
        {
            source.Scales[index] = scale;
            var field = source.Fields[source.FileColumns + index]; field.Original = scale.Name; field.Name = scale.Name;
        }
        else
        {
            source.Scales.Add(scale);
            source.Fields.Add(new Field { Original = scale.Name, Name = scale.Name, Kind = ValueKind.Number, Keep = true });
        }
        Refresh(source);
    }
    public static void Remove(Source source, ScaleScore scale)
    {
        int index = source.Scales.IndexOf(scale); if (index < 0) return;
        int column = source.FileColumns + index;
        source.Scales.RemoveAt(index); source.Fields.RemoveAt(column);
        source.KeepRules.RemoveAll(r => r.Column == column);
        for (int r = 0; r < source.Rows.Count; r++) source.Rows[r] = source.Rows[r].Where((_, c) => c != column).ToArray();
    }
}
