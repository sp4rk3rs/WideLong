using System.Globalization;

namespace WideLong;

public sealed record SortRule(string Key, bool Descending = false);

// Presentation choices live only in memory and never change source row order.
public sealed class ViewPreferences
{
    public List<string> ColumnOrder { get; set; } = [];
    public List<SortRule> Sorts { get; set; } = [];
    public Dictionary<string, string> Names { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string>? RowVariables { get; set; }
    public ViewPreferences Copy() => new() { ColumnOrder = [.. ColumnOrder], Sorts = [.. Sorts], Names = new(Names, StringComparer.Ordinal), RowVariables = RowVariables?.ToHashSet(StringComparer.Ordinal) };

    public void Reconcile(IEnumerable<string> keys)
    {
        var known = ColumnOrder.ToHashSet(StringComparer.Ordinal);
        foreach (string key in keys) if (known.Add(key)) ColumnOrder.Add(key);
    }
    public void ToggleSort(string key)
    {
        int index = Sorts.FindIndex(s => s.Key == key);
        if (index < 0) Sorts.Add(new(key)); else Sorts.RemoveAt(index);
    }
    public void MoveSort(string from, string to)
    {
        int oldIndex = Sorts.FindIndex(s => s.Key == from), target = Sorts.FindIndex(s => s.Key == to);
        if (oldIndex < 0 || target < 0 || oldIndex == target) return;
        var item = Sorts[oldIndex]; Sorts.RemoveAt(oldIndex); Sorts.Insert(target, item);
    }
    public void SetColumnOrder(IReadOnlyList<string> visibleKeys, bool updateSortPriority)
    {
        Reconcile(visibleKeys);
        var visible = visibleKeys.ToHashSet(StringComparer.Ordinal); int next = 0;
        for (int i = 0; i < ColumnOrder.Count; i++) if (visible.Contains(ColumnOrder[i])) ColumnOrder[i] = visibleKeys[next++];
        if (updateSortPriority)
        {
            var ordered = Sorts.Where(s => visible.Contains(s.Key)).OrderBy(s => visibleKeys.ToList().IndexOf(s.Key)).ToList();
            int rank = 0;
            for (int i = 0; i < Sorts.Count; i++) if (visible.Contains(Sorts[i].Key)) Sorts[i] = ordered[rank++];
        }
    }
    /// <summary>Places sorted variables at the far left in priority order; other columns keep their relative order.</summary>
    public void MoveSortsToFront()
    {
        var sorted = Sorts.Select(s => s.Key).ToList();
        var set = sorted.ToHashSet(StringComparer.Ordinal);
        ColumnOrder = [.. sorted, .. ColumnOrder.Where(k => !set.Contains(k))];
    }
    public void ReorderColumns(IReadOnlyList<string> visibleKeys, string movedKey) => SetColumnOrder(visibleKeys, Sorts.Any(s => s.Key == movedKey));
}

public static class Presentation
{
    public static string Identity(OutputField field, int index) => field.Key.Length > 0 ? field.Key : field.Source + "\u001f" + field.Name + "\u001f" + index;

    /// <summary>Ranks matches: name starts with the text, then name contains it, then the source file matches.</summary>
    public static List<OutputField> SortCandidates(IEnumerable<OutputField> fields, IEnumerable<string> sortedKeys, string query, int limit = 200)
    {
        var sorted = sortedKeys.ToHashSet(StringComparer.Ordinal);
        var available = fields.Where(f => !sorted.Contains(f.Key));
        query = query.Trim();
        if (query.Length == 0) return available.Take(limit).ToList();
        return available.Select((f, i) => (Field: f, Index: i, Rank: f.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : f.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1 : f.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : 3))
            .Where(x => x.Rank < 3).OrderBy(x => x.Rank).ThenBy(x => x.Index).Take(limit).Select(x => x.Field).ToList();
    }

    public static View Apply(View input, ViewPreferences preferences, string filter = "")
    {
        var keys = input.Fields.Select(Identity).ToArray();
        preferences.Reconcile(keys);
        var ordering = preferences.ColumnOrder.Select((key, i) => (key, i)).ToDictionary(x => x.key, x => x.i, StringComparer.Ordinal);
        var columns = Enumerable.Range(0, keys.Length).OrderBy(i => ordering[keys[i]]).ToArray();
        var sorts = preferences.Sorts.Select(rule => (Rule: rule, Index: Array.IndexOf(keys, rule.Key))).Where(x => x.Index >= 0).ToArray();
        IEnumerable<(Cell[] Row, int OriginalIndex)> rows = input.Rows.Select((row, i) => (row, i));
        if (filter.Length > 0) rows = rows.Where(x => x.Row.Any(c => !c.Missing && c.Value.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        if (sorts.Length > 0)
        {
            // Parse numeric sort keys once per row rather than on every comparison.
            var candidates = rows.ToList();
            var numbers = sorts.Select(s => input.Fields[s.Index].Kind == ValueKind.Number
                ? candidates.Select(x => !x.Row[s.Index].Missing && decimal.TryParse(x.Row[s.Index].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal n) ? n : (decimal?)null).ToArray()
                : null).ToArray();
            var order = Enumerable.Range(0, candidates.Count).ToArray();
            Array.Sort(order, (a, b) =>
            {
                for (int k = 0; k < sorts.Length; k++)
                {
                    var (rule, index) = sorts[k];
                    Cell ac = candidates[a].Row[index], bc = candidates[b].Row[index];
                    // Blanks stay last in either direction; equal values retain import order.
                    if (ac.Missing != bc.Missing) return ac.Missing ? 1 : -1;
                    if (ac.Missing) continue;
                    int comparison;
                    if (numbers[k] is { } parsed)
                    {
                        decimal? an = parsed[a], bn = parsed[b];
                        // Within Number columns, valid numbers precede any unparseable text so the order is total.
                        comparison = an.HasValue && bn.HasValue ? an.Value.CompareTo(bn.Value) : an.HasValue != bn.HasValue ? (an.HasValue ? -1 : 1) : StringComparer.OrdinalIgnoreCase.Compare(ac.Value, bc.Value);
                    }
                    else comparison = StringComparer.OrdinalIgnoreCase.Compare(ac.Value, bc.Value);
                    if (comparison != 0) return rule.Descending ? -comparison : comparison;
                }
                return candidates[a].OriginalIndex.CompareTo(candidates[b].OriginalIndex);
            });
            rows = order.Select(i => candidates[i]);
        }
        var fields = columns.Select(i => input.Fields[i] with { Key = keys[i], Name = preferences.Names.TryGetValue(keys[i], out string? name) ? name : input.Fields[i].Name }).ToList();
        if (fields.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Count) throw new InvalidOperationException("Two output columns have the same name. Rename one before exporting.");
        bool identity = columns.Select((c, i) => c == i).All(x => x);
        // Cells are immutable, so rows can be shared when the column order is unchanged.
        return new() { Fields = fields, Rows = identity ? rows.Select(x => x.Row).ToList() : rows.Select(x => columns.Select(i => x.Row[i]).ToArray()).ToList(), Notes = [.. input.Notes] };
    }
}
