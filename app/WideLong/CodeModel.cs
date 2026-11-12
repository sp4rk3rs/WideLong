using System.Globalization;
using System.IO;
using System.Text;

namespace WideLong;

// Language-neutral description of a cleaning script. CodeModel.Build replays every recorded step on the
// original rows, so each operation below is known to reproduce the application's values; the R and SAS
// writers only decide how to spell them.
internal abstract record CleanOp;
internal sealed record RenameOp(List<(int Col, string From, string To)> Items) : CleanOp;
internal sealed record CellOp(int Col, int Row, ValueKind Kind, string Before, string After) : CleanOp;
internal sealed record ReplaceOp(int Col, ValueKind Kind, string Before, string After) : CleanOp;
internal sealed record ConvertOp(List<int> Cols, ValueKind From, ValueKind Target, string DateFormat, string[] Tokens) : CleanOp;
internal sealed record MissingOp(List<int> Cols, ValueKind Kind, string[] Tokens) : CleanOp;
internal sealed record KeepRuleSpec(int Col, ValueKind Kind, string[] Values, bool KeepMissing, string[] Removed, bool RemovesMissing);
internal sealed record KeepOp(List<KeepRuleSpec> Rules) : CleanOp;
internal sealed record SelectOp(List<int> Keep, List<int> Drop) : CleanOp;
/// <summary>A type change on a matching key. Keys stay text while files are combined, so the values are rewritten to the
/// application's canonical text for that type (00001 → "1", 3/5/2025 → "2025-03-05", yes → "true"); the final step types them.</summary>
internal sealed record KeyNormalizeOp(int Col, ValueKind Target, string DateFormat) : CleanOp;
/// <summary>A scale score (derived column Col) from numeric item columns, after missing-value codes are applied.</summary>
internal sealed record ScaleOp(int Col, ScaleScore Scale, List<int> Items) : CleanOp;

internal sealed class FilePlan
{
    public required Source Source { get; init; }
    public int Index { get; init; }
    public List<CleanOp> Ops { get; } = [];
    /// <summary>Working type of each column once its file is cleaned. Matching keys stay Text until the end.</summary>
    public ValueKind[] Kinds { get; set; } = [];
    /// <summary>Kept, non-key columns that are not repeated across times.</summary>
    public List<int> Basic { get; } = [];
    public List<string> Measures { get; } = [];
    public List<string> Occasions { get; } = [];
    public Dictionary<(string Measure, string Occasion), int> Repeated { get; } = new();
    public bool Wide => Measures.Count > 0;
    public bool HasEntity => Source.EntityColumn >= 0;
    public bool HasTimeColumn => Source.TimeColumn >= 0;
    public bool Timed => HasTimeColumn || Wide;
    /// <summary>Largest UTF-8 byte length of each column across original values, every recorded step and the result.</summary>
    public int[] Widths { get; set; } = [];
    public ValueKind MeasureKind(string measure) => Source.Fields[Repeated.First(r => r.Key.Measure == measure).Value].Kind;
}

internal sealed class CodeModel
{
    public required CodePlan Plan { get; init; }
    /// <summary>The chosen layout before filters, sorting and renaming.</summary>
    public required View Raw { get; init; }
    public required View Final { get; init; }
    public required List<FilePlan> Files { get; init; }
    public required string IdName { get; init; }
    public required string GroupName { get; init; }
    public string? EntityName { get; init; }
    public bool HasEntity => EntityName != null;
    public required Engine.KeyKindSet KeyKinds { get; init; }
    public Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, CodeVariable> VariablesByKey { get; } = new(StringComparer.Ordinal);

    /// <summary>The release version, set once in Directory.Build.props.</summary>
    public static readonly string Version = (typeof(CodeModel).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public string Label(FilePlan file, int column) => Plan.Stack ? Labels["stack:" + file.Source.Fields[column].Name] : Labels[file.Source.Key + ":" + column];
    public string MeasureLabel(FilePlan file, string measure) => Plan.Stack ? Labels["stack:" + measure] : Labels[file.Source.Key + ":measure:" + measure];
    public string VariableKey(FilePlan file, int column) => Plan.Stack ? "stack:" + file.Source.Fields[column].Name : file.Source.Key + ":" + column;
    public string MeasureKey(FilePlan file, string measure) => Plan.Stack ? "stack:" + measure : file.Source.Key + ":measure:" + measure;
    public string RawName(string key) => Raw.Fields.First(f => f.Key == key).Name;
    public OutputField? RawField(string key) => Raw.Fields.FirstOrDefault(f => f.Key == key);

    public static CodeModel Build(CodePlan plan)
    {
        var session = plan.Session;
        var raw = plan.Layout == Layout.Source ? Engine.SourceView(session.Sources[plan.SourceIndex]) : Engine.Build(session, plan.Layout, plan.Match, plan.Stack, plan.Preferences.RowVariables);
        var final = Presentation.Apply(Filtering.Apply(raw, plan.StructuredFilter), plan.Preferences.Copy(), plan.Filter);
        var indexes = plan.Layout == Layout.Source ? [plan.SourceIndex] : Enumerable.Range(0, session.Sources.Count).ToList();
        var model = new CodeModel
        {
            Plan = plan, Raw = raw, Final = final,
            Files = indexes.Select(i => Clean(session.Sources[i], i)).ToList(),
            IdName = SchemaNaming.IdName(session), GroupName = SchemaNaming.GroupName(session),
            EntityName = session.HasEntity ? SchemaNaming.EntityName(session) : null,
            KeyKinds = Engine.KeyKinds(session)
        };
        foreach (var v in plan.Variables) { model.Labels[v.Key] = v.Name; model.VariablesByKey[v.Key] = v; }
        return model;
    }

    private static string[] Present(List<string[]> rows, int column, IEnumerable<string> tokens) =>
        tokens.Where(t => t.Length > 0 && rows.Any(r => r[column] == t)).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToArray();

    private static void AddConvert(FilePlan file, int column, ValueKind from, ValueKind target, string format, string[] tokens)
    {
        if (from == target && tokens.Length == 0) return;
        if (file.Ops.Count > 0 && file.Ops[^1] is ConvertOp last && last.From == from && last.Target == target && last.DateFormat == format && last.Tokens.SequenceEqual(tokens) && !last.Cols.Contains(column))
        { last.Cols.Add(column); return; }
        file.Ops.Add(new ConvertOp([column], from, target, format, tokens));
    }

    private static FilePlan Clean(Source s, int index)
    {
        var file = new FilePlan { Source = s, Index = index };
        int n = s.Fields.Count;
        bool[] used = Enumerable.Range(0, n).Select(c => s.Fields[c].Keep || s.IsKeyColumn(c) || s.KeepRules.Any(r => r.Column == c)).ToArray();
        var kinds = new ValueKind[n];
        // Scale scores are derived columns after the file's own columns; they start empty and are computed below.
        var rows = s.OriginalRows!.Select(r => { var copy = new string[n]; Array.Fill(copy, ""); Array.Copy(r, copy, Math.Min(r.Length, n)); return copy; }).ToList();
        var widths = Enumerable.Range(0, n).Select(c => Math.Max(1, rows.Select(r => Encoding.UTF8.GetByteCount(r[c])).DefaultIfEmpty(1).Max())).ToArray();
        void Widen(int c, string value) => widths[c] = Math.Max(widths[c], Encoding.UTF8.GetByteCount(value));

        var renames = Enumerable.Range(0, n).Where(c => used[c] && s.Fields[c].Name != s.Fields[c].Original).Select(c => (c, s.Fields[c].Original, s.Fields[c].Name)).ToList();
        if (renames.Count > 0) file.Ops.Add(new RenameOp(renames));
        foreach (var step in s.Steps)
        {
            int c = step.Column; bool emit = used[c];
            if (step.Kind == StepKind.Cell)
            {
                if (emit) file.Ops.Add(new CellOp(c, step.Row, kinds[c], step.Before, step.After));
                rows[step.Row][c] = step.After; Widen(c, step.After);
            }
            else if (step.Kind == StepKind.Replace)
            {
                if (emit && rows.Any(r => r[c] == step.Before)) file.Ops.Add(new ReplaceOp(c, kinds[c], step.Before, step.After));
                foreach (var r in rows) if (r[c] == step.Before) r[c] = step.After;
                Widen(c, step.After);
            }
            else
            {
                var tokens = step.MissingTokens ?? [""];
                // Matching keys stay text until the final step, but a conversion still rewrites their values
                // (00001 → 1, 3/5/2025 → 2025-03-05). Repeat it as canonical text so keys match like they do in the app.
                if (emit && s.IsKeyColumn(c)) { if (step.Target != ValueKind.Text && rows.Any(r => Values.ConvertWithMissing(r[c], step.Target, step.DateFormat, tokens) != r[c])) file.Ops.Add(new KeyNormalizeOp(c, step.Target, step.DateFormat)); }
                else if (emit) { AddConvert(file, c, kinds[c], step.Target, step.DateFormat, step.Target == ValueKind.Text ? [] : Present(rows, c, tokens)); kinds[c] = step.Target; }
                foreach (var r in rows) { r[c] = Values.ConvertWithMissing(r[c], step.Target, step.DateFormat, tokens); Widen(c, r[c]); }
            }
        }
        // Variables whose type was set without a recorded step (built-in examples) are typed here.
        for (int c = s.FileColumns; c < n; c++) kinds[c] = ValueKind.Number;
        for (int c = 0; c < n; c++)
        {
            if (!used[c] || s.IsKeyColumn(c) || s.IsDerived(c) || s.Fields[c].Kind == kinds[c]) continue;
            var tokens = s.EffectiveMissingTokens(c).ToArray();
            AddConvert(file, c, kinds[c], s.Fields[c].Kind, "yyyy-MM-dd", s.Fields[c].Kind == ValueKind.Text ? [] : Present(rows, c, tokens));
            foreach (var r in rows) { try { r[c] = Values.ConvertWithMissing(r[c], s.Fields[c].Kind, "yyyy-MM-dd", tokens); } catch (InvalidOperationException) { } }
            kinds[c] = s.Fields[c].Kind;
        }
        var missing = new List<MissingOp>();
        for (int c = 0; c < n; c++)
        {
            if (!used[c]) continue;
            var tokens = s.EffectiveMissingTokens(c).Where(t => (kinds[c] == ValueKind.Text || t.Length > 0) && rows.Any(r => r[c] == t)).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToArray();
            if (tokens.Length == 0) continue;
            var group = missing.FirstOrDefault(g => g.Kind == kinds[c] && g.Tokens.SequenceEqual(tokens));
            if (group != null) group.Cols.Add(c); else missing.Add(new MissingOp([c], kinds[c], tokens));
        }
        file.Ops.AddRange(missing.Where(op => op.Cols.All(c => !s.IsDerived(c))));
        // Scale scores use the cleaned, typed items with missing-value codes removed, exactly as the application computes them.
        Scales.Compute(s, rows);
        for (int i = 0; i < s.Scales.Count; i++)
        {
            int c = s.FileColumns + i; foreach (var r in rows) Widen(c, r[c]);
            var items = Scales.ItemColumns(s, s.Scales[i]);
            if (items.Any(x => x < 0 || kinds[x] != ValueKind.Number)) throw new InvalidOperationException($"The scale {s.Fields[c].Name} needs numeric items. Convert its items to Number again, or remove the scale.");
            if (used[c]) file.Ops.Add(new ScaleOp(c, s.Scales[i], items));
        }
        if (s.KeepRules.Count > 0)
            file.Ops.Add(new KeepOp(s.KeepRules.Select(rule =>
            {
                var removed = s.Rows.Select(r => r[rule.Column]).Where(v => !s.IsMissing(v, rule.Column) && !rule.Values.Contains(v, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
                bool removesMissing = !rule.KeepMissing && s.Rows.Any(r => s.IsMissing(r[rule.Column], rule.Column));
                return new KeepRuleSpec(rule.Column, kinds[rule.Column], rule.Values, rule.KeepMissing, removed, removesMissing);
            }).ToList()));
        var keep = Enumerable.Range(0, n).Where(c => s.Fields[c].Keep || s.IsKeyColumn(c)).ToList();
        var drop = Enumerable.Range(0, n).Where(c => !keep.Contains(c) && !s.IsDerived(c)).ToList();  // unkept scale scores are never created
        if (drop.Count > 0) file.Ops.Add(new SelectOp(keep, drop));
        for (int c = 0; c < n; c++)
        {
            var f = s.Fields[c];
            if (!f.Keep || s.IsKeyColumn(c)) continue;
            if (f.Measure.Length == 0) { file.Basic.Add(c); continue; }
            if (!file.Measures.Contains(f.Measure)) file.Measures.Add(f.Measure);
            if (!file.Occasions.Contains(f.Occasion)) file.Occasions.Add(f.Occasion);
            file.Repeated[(f.Measure, f.Occasion)] = c;
        }
        file.Kinds = kinds; file.Widths = widths;
        return file;
    }

    // ---- Shared text helpers ----
    public static string Show(string value)
    {
        string text = value.Replace("\r", " ").Replace("\n", " ").Replace("*/", "* /");
        return value.Length == 0 ? "blank" : "\"" + (text.Length > 40 ? text[..40] + "…" : text) + "\"";
    }
    public static string ShowList(IEnumerable<string> values, int max = 5)
    {
        var list = values.ToList();
        return string.Join(", ", list.Take(max).Select(Show)) + (list.Count > max ? $" and {list.Count - max:N0} more" : "");
    }
    public static string KindWord(ValueKind kind) => kind switch { ValueKind.Number => "numbers", ValueKind.Date => "dates", ValueKind.Boolean => "true/false values", _ => "text" };
    public static string ShapeWord(Layout layout) => layout switch { Layout.Person => "wide: one row per ID", Layout.PersonTime => "long: one row per ID and time", Layout.Measurement => "custom variable/value rows", _ => "one file's rows" };

    /// <summary>Breaks a long statement after commas (R) or spaces (SAS), never inside quotes or a trailing R comment.</summary>
    public static string Wrap(string line, bool sas, int width = 110)
    {
        // Comments stay on one line; short overflows are not worth a dangling continuation line.
        string trimmed = line.TrimStart();
        if (line.Length <= width + 25 || trimmed.StartsWith('#') || trimmed.StartsWith("/*", StringComparison.Ordinal)) return line;
        string lead = new string(' ', line.Length - line.TrimStart().Length) + "    ";
        var b = new StringBuilder(); int start = 0, lastBreak = -1, depth = 0; char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (quote != '\0') { if (!sas && ch == '\\') { i++; continue; } if (ch == quote) quote = '\0'; continue; }
            if (ch is '"' or '\'' or '`') { quote = ch; continue; }
            if (!sas && ch == '#') break;
            if (ch == '(') depth++; else if (ch == ')') depth--;
            bool candidate = sas ? ch == ' ' && i > 0 && line[i - 1] != ' ' && i > lead.Length : ch == ' ' && i > 0 && line[i - 1] == ',' && depth > 0;
            if (candidate && i - start > width && lastBreak > start) { b.Append(line, start, lastBreak - start).Append('\n').Append(lead); start = lastBreak + 1; }
            if (candidate) lastBreak = i;
        }
        if (line.Length - start > width + 25 && lastBreak > start) { b.Append(line, start, lastBreak - start).Append('\n').Append(lead); start = lastBreak + 1; }
        return b.Append(line, start, line.Length - start).ToString();
    }

    /// <summary>Folder shared by every imported original, or null when files come from different folders or are examples.</summary>
    public string? CommonFolder(bool portable)
    {
        if (portable || Files.Any(f => f.Source.ImportPath == null)) return null;
        var folders = Files.Select(f => Path.GetDirectoryName(f.Source.ImportPath!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return folders.Count == 1 ? folders[0] : null;
    }
    /// <summary>The file name on disk. A file imported twice shows as "data (2).csv" in WideLong but is still read from data.csv.</summary>
    public static string DiskName(FilePlan file, bool portable) => portable || file.Source.ImportPath == null ? file.Source.Name : Path.GetFileName(file.Source.ImportPath);
    public string FileReference(FilePlan file, bool portable) => portable || file.Source.ImportPath == null || CommonFolder(portable) != null ? DiskName(file, portable) : file.Source.ImportPath;

    /// <summary>Lower-case object/dataset names from file names, unique and not clashing with names the scripts use.</summary>
    public List<string> ObjectNames(int maxLength, IEnumerable<string> reserved)
    {
        var used = new HashSet<string>(reserved, StringComparer.OrdinalIgnoreCase); var names = new List<string>();
        foreach (var file in Files)
        {
            string stem = System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(file.Source.Name).ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
            if (stem.Length == 0 || !char.IsLetter(stem[0])) stem = "file_" + stem;
            stem = stem[..Math.Min(stem.Length, maxLength)].TrimEnd('_'); string name = stem; int k = 2;
            while (!used.Add(name) || !used.Add(name + "_long") || !used.Add(name + "_part")) { string suffix = "_" + k++; name = stem[..Math.Min(stem.Length, maxLength - suffix.Length)] + suffix; }
            names.Add(name);
        }
        return names;
    }
    public static string Today => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
