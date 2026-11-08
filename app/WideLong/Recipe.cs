using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace WideLong;

public sealed record CodeVariable(string Key, string Name, ValueKind Kind, string Source, bool Stacked);
public sealed record CodeColumn(OutputField Field, string Role, int Variable = 0, string? Time = null, int SourceColumn = -1, string? Entity = null);
public sealed class CodePlan
{
    public required Session Session { get; init; }
    public Layout Layout { get; init; }
    public MatchMode Match { get; init; }
    public bool Stack { get; init; }
    public int SourceIndex { get; init; }
    public required ViewPreferences Preferences { get; init; }
    public string Filter { get; init; } = "";
    public RowFilterSpec StructuredFilter { get; init; } = new();
    public required List<CodeVariable> Variables { get; init; }
    public required List<CodeColumn> Columns { get; init; }
    public int ProvenanceVariable { get; init; }
    public required List<int[]> SourceVariables { get; init; }
    public required List<(int Column, bool Descending)> Sorts { get; init; }
}

public static class Recipe
{
    public static CodePlan Plan(Session session, Layout layout, MatchMode match, bool stack, string? sourceKey, ViewPreferences preferences, string filter, RowFilterSpec? structuredFilter=null)
    {
        if (session.Sources.Count == 0) throw new InvalidOperationException("Import files or choose an example first.");
        var copy = session.Copy();
        foreach (var s in copy.Sources)
        {
            if (s.OriginalRows == null) throw new InvalidOperationException("The original source snapshot is unavailable. Re-import the original file to generate reproducible code.");
            var replay = Replay(s, validateForCode: true);
            if (replay.Count != s.Rows.Count || replay.Where((r, i) => !r.SequenceEqual(s.Rows[i], StringComparer.Ordinal)).Any()) throw new InvalidOperationException("A working value has no recorded cleaning step. Re-import and make the correction again before exporting code.");
        }
        int sourceIndex = Math.Max(0, copy.Sources.FindIndex(s => s.Key == sourceKey));
        var raw = layout == Layout.Source ? Engine.SourceView(copy.Sources[sourceIndex]) : Engine.Build(copy, layout, match, stack, preferences.RowVariables);
        var final = Presentation.Apply(Filtering.Apply(raw,structuredFilter), preferences.Copy(), filter);
        var baseView = layout == Layout.Source ? new View() : Engine.Build(copy, Layout.PersonTime, match, stack);
        var stackedKeys = preferences.RowVariables ?? new HashSet<string>(StringComparer.Ordinal);
        var variables = baseView.Fields.Where(f => !f.Key.StartsWith('@')).Select(f => new CodeVariable(f.Key, f.Name, f.Kind, f.Source, stackedKeys.Contains(f.Key))).ToList();
        int Var(string key) => variables.FindIndex(v => v.Key == key) + 1;
        var mappings = new List<int[]>();
        foreach (var source in copy.Sources)
            mappings.Add(source.Fields.Select((f, c) => !f.Keep || c == source.IdColumn || c == source.EntityColumn || c == source.TimeColumn ? 0 : Var(stack ? "stack:" + (f.Measure.Length > 0 ? f.Measure : f.Name) : source.Key + (f.Measure.Length > 0 ? ":measure:" + f.Measure : ":" + c))).ToArray());
        var columns = new List<CodeColumn>();
        foreach (var field in final.Fields)
        {
            if (layout == Layout.Source) { int index = int.Parse(field.Key[(copy.Sources[sourceIndex].Key.Length + 1)..], CultureInfo.InvariantCulture); columns.Add(new(field, "source_col", SourceColumn: index)); continue; }
            if (field.Key.StartsWith('@')) { columns.Add(new(field, field.Key == "@person_id" ? "id" : field.Key[1..])); continue; }
            var parts = field.Key.Split("\u001ftime:", 2, StringSplitOptions.None);
            var entityParts = parts[0].Split("\u001fentity:", 2, StringSplitOptions.None);
            int variable = Var(entityParts[0]);
            if (variable == 0) throw new InvalidOperationException("This output column cannot be mapped back to a source variable.");
            columns.Add(new(field, "var", variable, parts.Length == 2 ? parts[1] : null, Entity: entityParts.Length == 2 ? entityParts[1] : null));
        }
        var sorts = preferences.Sorts.Select(s => (Column: columns.FindIndex(c => c.Field.Key == s.Key), s.Descending)).Where(s => s.Column >= 0).ToList();
        return new() { Session = copy, Layout = layout, Match = match, Stack = stack, SourceIndex = sourceIndex, Preferences = preferences.Copy(), Filter = filter, StructuredFilter=structuredFilter?.Copy()??new(), Variables = variables, Columns = columns, SourceVariables = mappings, ProvenanceVariable = variables.FindIndex(v => v.Source == "provenance") + 1, Sorts = sorts };
    }

    public static List<string[]> Replay(Source source, bool validateForCode = false)
    {
        var rows = (source.OriginalRows ?? throw new InvalidOperationException("No original source snapshot.")).Select(r => (string[])r.Clone()).ToList();
        foreach (var step in source.Steps)
        {
            if (step.Kind == StepKind.Cell)
            {
                if (step.Row < 0 || step.Row >= rows.Count || rows[step.Row][step.Column] != step.Before) throw new InvalidOperationException("A correction's original record no longer matches.");
                rows[step.Row][step.Column] = step.After;
            }
            else if (step.Kind == StepKind.Replace) { foreach (var row in rows) if (row[step.Column] == step.Before) row[step.Column] = step.After; }
            else
            {
                foreach (var row in rows)
                {
                    string value = Values.ConvertWithMissing(row[step.Column], step.Target, step.DateFormat, step.MissingTokens);
                    if (validateForCode && step.Target == ValueKind.Number && value.Length > 0 && (value.Contains('E') || value.Count(char.IsDigit) > 15))
                        throw new InvalidOperationException("A numeric cleaning step uses precision or exponent notation not yet reproduced exactly by code export. Keep those values as Text from the original file, or export the prepared dataset instead.");
                    row[step.Column] = value;
                }
            }
        }
        return rows;
    }

    public static string RQuote(string text)
    {
        var b=new StringBuilder("\"");
        foreach(var rune in text.EnumerateRunes())
            b.Append(rune.Value switch { 0 => throw new InvalidOperationException("R code cannot represent NUL characters."), 34 => "\\\"", 92 => "\\\\", 13 => "\\r", 10 => "\\n", 9 => "\\t", <32 => "\\u"+rune.Value.ToString("X4"), >127 => (rune.Value>65535?"\\U"+rune.Value.ToString("X8"):"\\u"+rune.Value.ToString("X4")), _ => rune.ToString() });
        return b.Append('"').ToString();
    }
    public static string SasQuote(string text) => "'" + text.Replace("'", "''") + "'";
    private static string RV(IEnumerable<string> values) => "c(" + string.Join(",", values.Select(RQuote)) + ")";
    private static string RI(IEnumerable<int> values) => "c(" + string.Join(",", values) + ")";
    public static string RDateFormat(string value)
    {
        if (Regex.IsMatch(value, @"[^yMd/ .-]")) throw new InvalidOperationException("Code export supports numeric year/month/day date patterns. Use yyyy-MM-dd, MM/dd/yyyy, or dd/MM/yyyy.");
        return Regex.Replace(value, "yyyy|yy|MM|M|dd|d", m => m.Value switch { "yyyy" => "%Y", "yy" => "%y", "MM" or "M" => "%m", _ => "%d" });
    }

    /// <summary>A readable base-R script that rebuilds this dataset from the original files.</summary>
    public static string R(CodePlan plan, bool portablePaths = false, string datasetName = "prepared_data") => RCode.Generate(plan, portablePaths, datasetName);

    public static void SaveExampleBundle(string path, CodePlan plan)
    {
        if (plan.Session.Sources.Any(s => s.ImportPath != null)) throw new InvalidOperationException("Raw-file bundles are available only for built-in fictional examples. Imported originals are never copied into a code bundle.");
        string r = R(plan, true, "example"); string? sas = null; string? reason = null;
        try { sas = SasRecipe.Generate(plan, true, "example"); } catch (InvalidOperationException ex) { reason = ex.Message; }
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None); using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Entry(string name, Action<TextWriter> write) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); write(writer); }
        Entry("clean.R", w => w.Write(r));
        if (sas != null) Entry("clean.sas", w => w.Write(sas));
        foreach (var s in plan.Session.Sources)
            Entry(s.Name, w => Csv.Write(w, new View { Fields = s.Fields.Select(f => new OutputField(f.Original, ValueKind.Text, s.Name)).ToList(), Rows = s.OriginalRows!.Select(row => row.Select(v => new Cell(v)).ToArray()).ToList() }));
        Entry("README.txt", w => w.WriteLine("Fictional source files and reproducible cleaning scripts. Extract into one folder; run clean.R with that folder as the working directory. In clean.sas, set the folder macro variable to the extracted location. Raw tables remain separate from prepared. No cleaned CSV is required to run these scripts.\n" + (reason == null ? "" : "SAS script not included: " + reason)));
    }
}
