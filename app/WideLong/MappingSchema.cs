using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace WideLong;

public sealed class ColumnMapping : INotifyPropertyChanged
{
    public required int Column { get; init; }
    public required string Original { get; init; }
    public required string Name { get; init; }
    public required ValueKind Kind { get; init; }
    private bool keep;
    private string variable = "", grouping = "";
    public bool Keep { get => keep; set { keep = value; Notify(); } }
    public string Variable { get => variable; set { variable = value ?? ""; Notify(); } }
    public string Grouping { get => grouping; set { grouping = value ?? ""; Notify(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
}

public static class RepeatedColumnMapping
{
    public static List<ColumnMapping> FromSource(Source source) => source.Fields.Select((f, c) => (f, c)).Where(x => !source.IsKeyColumn(x.c)).Select(x => new ColumnMapping { Column = x.c, Original = x.f.Original, Name = x.f.Name, Kind = x.f.Kind, Keep = x.f.Keep, Variable = x.f.Measure, Grouping = x.f.Occasion }).ToList();
    public static string InferGrouping(string name)
    {
        var match = Regex.Match(name, @"^.+?[_\s](baseline|bl|followup|month_?\d+|m\d+|visit_?\d+|v\d+|t\d+|term_?\d+|wave_?\d+|trial_?\d+|year_?\d+|\d+)$", RegexOptions.IgnoreCase);
        if (match.Success) return match.Groups[1].Value;
        int split = name.LastIndexOf('_'); return split >= 0 && split + 1 < name.Length ? name[(split + 1)..] : "";
    }
    public static List<string> Validate(Source source, IEnumerable<ColumnMapping> mappings)
    {
        var rows = mappings.ToArray(); var errors = new List<string>();
        foreach (var row in rows)
        {
            if (row.Column < 0 || row.Column >= source.Fields.Count || source.IsKeyColumn(row.Column)) errors.Add("A key column or unknown column cannot be mapped as a repeated variable.");
            if (string.IsNullOrWhiteSpace(row.Variable) != string.IsNullOrWhiteSpace(row.Grouping)) errors.Add(row.Name + ": enter both a shared variable and grouping value, or clear both.");
        }
        foreach (var group in rows.Where(r => !string.IsNullOrWhiteSpace(r.Variable)).GroupBy(r => r.Variable.Trim(), StringComparer.Ordinal))
        {
            foreach (var duplicate in group.GroupBy(r => r.Grouping, StringComparer.Ordinal).Where(g => g.Count() > 1)) errors.Add(group.Key + ": grouping value '" + duplicate.Key + "' is assigned to more than one column.");
            if (group.Select(r => r.Kind).Distinct().Count() > 1) errors.Add(group.Key + ": repeated columns must share the same variable type. Change their source types first.");
        }
        if (rows.Select(r => r.Column).Distinct().Count() != rows.Length) errors.Add("Each source column must appear once in the mapping table.");
        return errors.Distinct(StringComparer.Ordinal).ToList();
    }
    public static void Apply(Source source, IEnumerable<ColumnMapping> mappings)
    {
        var rows = mappings.ToList(); var errors = Validate(source, rows);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
        foreach (var row in rows) { source.Fields[row.Column].Keep = row.Keep; source.Fields[row.Column].Measure = row.Variable.Trim(); source.Fields[row.Column].Occasion = string.IsNullOrWhiteSpace(row.Grouping) ? "" : row.Grouping; }
    }
}
