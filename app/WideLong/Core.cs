using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WideLong;

public enum ValueKind { Text, Number, Date, Boolean }
public enum Layout { PersonTime, Person, Measurement, Source }
public enum MatchMode { AllPeople, CommonPeople, FirstFilePeople }
public enum StepKind { Cell, Replace, Convert }
/// <param name="RestoreMissing">A Convert step that also set the variable's missing codes; PriorFieldMissing is what a revert restores.</param>
public sealed record CleaningStep(StepKind Kind, int Column, int Row = -1, string Before = "", string After = "", ValueKind Target = ValueKind.Text, string DateFormat = "yyyy-MM-dd", string[]? MissingTokens = null, bool RestoreMissing = false, string[]? PriorFieldMissing = null);
/// <summary>Keep only source records whose value in Column is one of Values (and missing values when KeepMissing). Applied before files are combined.</summary>
public sealed record KeepRule(int Column, string[] Values, bool KeepMissing);

public sealed class Field
{
    public string Original { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Keep { get; set; } = true;
    public ValueKind Kind { get; set; }
    public string Measure { get; set; } = "";
    public string Occasion { get; set; } = "";
    public HashSet<string>? MissingTokens { get; set; }
    public Field Copy() { var copy = (Field)MemberwiseClone(); copy.MissingTokens = MissingTokens == null ? null : new(MissingTokens, StringComparer.Ordinal); return copy; }
}

public sealed class Source
{
    public string Key { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<Field> Fields { get; set; } = [];
    public string? ImportPath { get; set; }
    public List<string[]> Rows { get; set; } = [];
    public List<string[]>? OriginalRows { get; set; }
    public List<CleaningStep> Steps { get; set; } = [];
    public List<KeepRule> KeepRules { get; set; } = [];
    public char Delimiter { get; set; } = ',';
    public string EncodingName { get; set; } = "UTF-8";
    public HashSet<string> MissingTokens { get; set; } = new(StringComparer.Ordinal) { "" };
    public bool IdChosen { get; set; }
    public bool GroupChosen { get; set; }
    public bool EntityChosen { get; set; }
    public string GroupReason { get; set; } = "Choose a column that distinguishes repeated records for an ID, if needed.";
    public int IdColumn { get; set; } = -1;
    public int TimeColumn { get; set; } = -1;
    public int EntityColumn { get; set; } = -1;
    public string EntityReason { get; set; } = "Choose an additional key when several people, devices, or other entities share the same ID and time.";
    public string IdReason { get; set; } = "Choose the column containing the ID.";
    public bool IsMissing(string value) => MissingTokens.Contains(value);
    public IReadOnlySet<string> EffectiveMissingTokens(int column) => Fields[column].MissingTokens ?? MissingTokens;
    public bool IsMissing(string value, int column) => EffectiveMissingTokens(column).Contains(value) || (Fields[column].Kind != ValueKind.Text && value.Length == 0);
    public bool IsKeyColumn(int column) => column == IdColumn || column == EntityColumn || column == TimeColumn;
    public void CaptureOriginal() => OriginalRows ??= Rows.Select(r => (string[])r.Clone()).ToList();
    /// <summary>True when a data record passes every response filter ("keep rule") of this file.</summary>
    public bool Keeps(int row) { foreach (var rule in KeepRules) { string v = Rows[row][rule.Column]; if (IsMissing(v, rule.Column) ? !rule.KeepMissing : Array.IndexOf(rule.Values, v) < 0) return false; } return true; }
    /// <param name="includeOriginals">False for preview snapshots, which never read the original rows.</param>
    public Source Copy(bool includeOriginals = true) => new() { Key = Key, Name = Name, ImportPath = ImportPath, Fields = Fields.Select(f => f.Copy()).ToList(), Rows = Rows.Select(r => (string[])r.Clone()).ToList(), OriginalRows = includeOriginals ? OriginalRows?.Select(r => (string[])r.Clone()).ToList() : null, Steps = Steps.Select(s => s with { MissingTokens = s.MissingTokens?.ToArray() }).ToList(), KeepRules = [.. KeepRules], Delimiter = Delimiter, EncodingName = EncodingName, MissingTokens = new(MissingTokens, StringComparer.Ordinal), IdChosen = IdChosen, GroupChosen = GroupChosen, EntityChosen = EntityChosen, GroupReason = GroupReason, EntityReason = EntityReason, IdColumn = IdColumn, TimeColumn = TimeColumn, EntityColumn = EntityColumn, IdReason = IdReason };
}

public sealed class Session
{
    public List<Source> Sources { get; set; } = [];
    public bool HasEntity => Sources.Any(source => source.EntityColumn >= 0);
    public Session Copy(bool includeOriginals = true) => new() { Sources = Sources.Select(s => s.Copy(includeOriginals)).ToList() };
}

public sealed record Origin(string SourceKey, int Row, int Column);
public sealed record Cell(string Value, Origin? Origin = null, bool? MissingFlag = null)
{
    /// <summary>Shared immutable cell for values a record does not have; wide files leave most combined cells empty.</summary>
    public static readonly Cell Empty = new("");
    public bool Missing => MissingFlag ?? Value.Length == 0;
}
public sealed record OutputField(string Name, ValueKind Kind, string Source, string Key = "");
public sealed class View
{
    public List<OutputField> Fields { get; set; } = [];
    public List<Cell[]> Rows { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

public static class Csv
{
    // RFC-4180 style quoting, embedded newlines, strict row width; no type inference.
    public static List<string[]> Parse(string text, char delimiter = ',')
    {
        if (text.Length > 0 && text[0] == '\ufeff') text = text[1..];
        var result = new List<string[]>();
        var row = new List<string>();
        var value = new StringBuilder();
        bool quoted = false, afterQuote = false, started = false;
        void FinishRow()
        {
            row.Add(value.ToString()); value.Clear();
            if (started || row.Count > 1 || row[0].Length > 0) result.Add(row.ToArray());
            row.Clear(); started = false; afterQuote = false;
        }
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                    else { quoted = false; afterQuote = true; }
                }
                else value.Append(ch);
                continue;
            }
            if (ch == delimiter) { row.Add(value.ToString()); value.Clear(); afterQuote = false; started = true; }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                FinishRow();
            }
            else if (afterQuote) throw new InvalidOperationException("Unexpected text after a closing quote. Check the delimiter or CSV quoting.");
            else if (ch == '"')
            {
                if (value.Length != 0) throw new InvalidOperationException("A quote inside an unquoted CSV field must be escaped.");
                quoted = true; started = true;
            }
            else { value.Append(ch); started = true; }
        }
        if (quoted) throw new InvalidOperationException("The CSV ends inside a quoted value.");
        if (started || row.Count > 0 || value.Length > 0 || afterQuote) FinishRow();
        if (result.Count == 0) throw new InvalidOperationException("The CSV is empty.");
        int width = result[0].Length;
        for (int i = 1; i < result.Count; i++)
            if (result[i].Length != width) throw new InvalidOperationException($"Record {i + 1} has {result[i].Length} fields; the header has {width}. Check the delimiter.");
        return result;
    }

    public static Source Import(string text, string name, char delimiter = ',')
    {
        var records = Parse(text, delimiter);
        if (records[0].Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("Every column needs a nonempty header.");
        if (records[0].Distinct(StringComparer.OrdinalIgnoreCase).Count() != records[0].Length)
            throw new InvalidOperationException("The CSV has duplicate column names. Give each column a distinct header before importing.");
        var source = new Source { Name = name, Fields = records[0].Select(h => new Field { Original = h, Name = h }).ToList(), Rows = records.Skip(1).ToList() };
        source.Delimiter = delimiter; source.CaptureOriginal(); Inference.Suggest(source);
        return source;
    }

    public static string Escape(string value) => value.Length == 0 || value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    public static void Write(TextWriter writer, View view)
    {
        writer.WriteLine(string.Join(",", view.Fields.Select(f => Escape(f.Name))));
        foreach (var row in view.Rows) writer.WriteLine(string.Join(",", row.Select(c => Escape(c.Missing ? "" : c.Value))));
    }
}

public static class Inference
{
    public static void Suggest(Source source)
    {
        int bestScore = 0;
        for (int c = 0; c < source.Fields.Count; c++)
        {
            string n = source.Fields[c].Original.ToLowerInvariant().Replace(" ", "_");
            int score = n is "subject_id" or "participant_id" or "patient_id" or "person_id" ? 100 : n is "id" or "id_number" or "idnumber" ? 90 : Regex.IsMatch(n, "(^|_)(subject|participant|patient|person)($|_)") ? 70 : n.EndsWith("_id") ? 40 : 0;
            if (source.Rows.Any(r => string.IsNullOrWhiteSpace(r[c]))) score = 0;
            if (score > bestScore) { source.IdColumn = c; bestScore = score; }
            if (source.TimeColumn < 0 && n is "time" or "visit" or "visit_date" or "timepoint" or "occasion" or "wave" or "month") source.TimeColumn = c;
        }
        if (source.IdColumn >= 0)
        {
            int unique = source.Rows.Select(r => r[source.IdColumn]).Distinct(StringComparer.Ordinal).Count();
            source.IdReason = $"Suggested from the column name; {unique:N0} distinct IDs across {source.Rows.Count:N0} rows. Review this choice.";
        }
        // Infer only groups with at least two repeated columns; suggestions stay editable.
        var groups = new Dictionary<string, List<(int Index, string Time)>>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < source.Fields.Count; c++)
        {
            if (c == source.IdColumn || c == source.TimeColumn) continue;
            var m = Regex.Match(source.Fields[c].Original, @"^(.+?)[_\s](baseline|bl|followup|month_?\d+|m\d+|visit_?\d+|v\d+|t\d+|term_?\d+|wave_?\d+|trial_?\d+|year_?\d+)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            string measure = m.Groups[1].Value;
            if (!groups.ContainsKey(measure)) groups[measure] = [];
            groups[measure].Add((c, m.Groups[2].Value));
        }
        foreach (var group in groups.Where(g => g.Value.Count >= 2))
            foreach (var (index, time) in group.Value) { source.Fields[index].Measure = group.Key; source.Fields[index].Occasion = time; }
    }

    public static ValueKind SuggestedKind(IEnumerable<string> values)
    {
        var sample = values.Where(v => v.Length > 0).Take(1000).ToList();
        if (sample.Count == 0) return ValueKind.Text;
        if (sample.All(v => v is "true" or "false")) return ValueKind.Boolean;
        if (sample.All(v => DateTime.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))) return ValueKind.Date;
        if (sample.Any(v => Regex.IsMatch(v, @"^[+-]?0\d"))) return ValueKind.Text;
        if (sample.All(v => decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return ValueKind.Number;
        return ValueKind.Text;
    }
}

public static class Values
{
    public static string Convert(string value, ValueKind kind, string dateFormat = "yyyy-MM-dd")
    {
        if (value.Length == 0 || kind == ValueKind.Text) return value;
        if (kind == ValueKind.Number && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal n)) return n.ToString("G29", CultureInfo.InvariantCulture);
        if (kind == ValueKind.Date && DateTime.TryParseExact(value, dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d)) return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (kind == ValueKind.Boolean)
        {
            if (value.ToLowerInvariant() is "true" or "yes" or "1") return "true";
            if (value.ToLowerInvariant() is "false" or "no" or "0") return "false";
        }
        throw new InvalidOperationException($"Cannot convert this value to {kind}.");
    }

    public static List<(int Row, string Value)> Failures(Source s, int column, ValueKind kind, string dateFormat)
    {
        var errors = new List<(int, string)>();
        for (int r = 0; r < s.Rows.Count; r++)
            try { ConvertWithMissing(s.Rows[r][column], kind, dateFormat, s.EffectiveMissingTokens(column)); } catch (InvalidOperationException) { errors.Add((r + 2, s.Rows[r][column])); }
        return errors;
    }
    public static string ConvertWithMissing(string value, ValueKind kind, string dateFormat, IEnumerable<string>? tokens) => kind != ValueKind.Text && (tokens ?? [""]).Contains(value, StringComparer.Ordinal) ? "" : Convert(value, kind, dateFormat);

    public static ConversionPreview PreviewConversion(Source source, IEnumerable<int> columns, ValueKind target, string dateFormat)
    {
        int total = 0, missing = 0, newlyMissing = 0, leadingZeros = 0, changed = 0, invalid = 0;
        var examples = new List<ConversionExample>(); var seen = new HashSet<(int, string, string, bool)>();
        foreach (int c in columns)
            for (int r = 0; r < source.Rows.Count; r++)
            {
                total++; string before = source.Rows[r][c];
                bool wasMissing = source.IsMissing(before, c);
                try
                {
                    string after = ConvertWithMissing(before, target, dateFormat, source.EffectiveMissingTokens(c));
                    bool becomesMissing = source.EffectiveMissingTokens(c).Contains(after) || (target != ValueKind.Text && after.Length == 0);
                    if (becomesMissing) missing++;
                    if (!wasMissing && becomesMissing) newlyMissing++;
                    if (before != after) changed++;
                    bool losesZero = !wasMissing && !becomesMissing && target == ValueKind.Number && Regex.IsMatch(before.Trim(), @"^[+-]?0\d");
                    if (losesZero) leadingZeros++;
                    if (examples.Count(x => !x.Invalid) < 6 && seen.Add((c, before, after, becomesMissing))) examples.Add(new(source.Fields[c].Name, r + 2, before, after, becomesMissing, false));
                }
                catch (InvalidOperationException)
                {
                    invalid++;
                    if (examples.Count(x => x.Invalid) < 4 && seen.Add((c, before, "", false))) examples.Add(new(source.Fields[c].Name, r + 2, before, "Cannot convert", false, true));
                }
            }
        return new(total, changed, missing, newlyMissing, leadingZeros, invalid, examples);
    }

    public static List<(int Column, int Row, string Value)> ValidatePolicies(Source source)
    {
        var invalid = new List<(int, int, string)>();
        for (int c = 0; c < source.Fields.Count; c++)
        {
            if (source.Fields[c].Kind == ValueKind.Text) continue;
            foreach (var failure in Failures(source, c, source.Fields[c].Kind, "yyyy-MM-dd")) invalid.Add((c, failure.Row, failure.Value));
        }
        return invalid;
    }
}

public sealed record NumericCandidate(Source Source, int Column, int Numbers, int Values, string[] Codes);

/// <summary>Finds Text variables whose values are (almost) all numbers. A few non-numeric codes such as "not given" become missing.</summary>
public static class NumericDetection
{
    public const double Share = 0.8;
    public const int MaxCodes = 5;
    private static bool IsNumber(string v) => decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !Regex.IsMatch(v.Trim(), @"^[+-]?0\d") && v.Count(char.IsDigit) <= 15;
    public static NumericCandidate? Check(Source source, int column)
    {
        var field = source.Fields[column];
        if (field.Kind != ValueKind.Text) return null;
        var present = source.Rows.Select(r => r[column]).Where(v => !source.IsMissing(v, column)).ToList();
        int numbers = present.Count(IsNumber);
        if (numbers < 2 || numbers < present.Count * Share) return null;
        var codes = present.Where(v => !IsNumber(v)).Distinct(StringComparer.Ordinal).ToArray();
        // Values that merely look like numbers with leading zeros (IDs, ZIP codes) keep their text.
        if (codes.Length > MaxCodes || codes.Any(c => decimal.TryParse(c, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) return null;
        return new(source, column, numbers, present.Count, codes);
    }
    /// <summary>Non-key variables, plus key roles whose values are plain numbers in every file (key text is never rewritten).</summary>
    public static List<NumericCandidate> Find(Session session, IEnumerable<Source>? sources = null)
    {
        var scope = (sources ?? session.Sources).ToHashSet();
        var result = new List<NumericCandidate>();
        foreach (var source in session.Sources.Where(scope.Contains))
            for (int c = 0; c < source.Fields.Count; c++)
                if (!source.IsKeyColumn(c) && Check(source, c) is { } candidate) result.Add(candidate);
        // Repeated columns of one measure (score_wave1, score_wave2) must share a type, so they convert together or not at all.
        var offered = result.Select(c => (c.Source, c.Column)).ToHashSet();
        result.RemoveAll(c => c.Source.Fields[c.Column].Measure is { Length: > 0 } measure && c.Source.Fields.Select((f, i) => (f, i)).Any(x => x.f.Measure == measure && x.f.Kind == ValueKind.Text && !offered.Contains((c.Source, x.i))));
        Func<Source, int>[] roles = [s => s.IdColumn, s => s.EntityColumn, s => s.TimeColumn];
        for (int index = 0; index < roles.Length; index++)
        {
            var role = roles[index];
            var keys = session.Sources.Select(s => (Source: s, Column: role(s))).Where(x => x.Column >= 0 && x.Column < x.Source.Fields.Count).ToList();
            if (keys.Count == 0 || !keys.Any(k => scope.Contains(k.Source)) || keys.Any(k => k.Source.Fields[k.Column].Kind != ValueKind.Text)) continue;
            bool canonical = keys.All(k => k.Source.Rows.Count > 0 && k.Source.Rows.All(r => IsNumber(r[k.Column]) && Values.Convert(r[k.Column], ValueKind.Number) == r[k.Column]));
            // Grouping labels taken from repeated column names must also be plain numbers.
            if (canonical && index == 2 && session.Sources.Any(s => s.Fields.Any(f => f.Keep && f.Measure.Length > 0 && !(IsNumber(f.Occasion) && Values.Convert(f.Occasion, ValueKind.Number) == f.Occasion)))) canonical = false;
            if (canonical) result.AddRange(keys.Select(k => new NumericCandidate(k.Source, k.Column, k.Source.Rows.Count, k.Source.Rows.Count, [])));
        }
        return result;
    }
    /// <summary>Converts the candidates to Number as recorded cleaning steps; their codes are added to the variable's missing values.</summary>
    public static void Apply(IEnumerable<NumericCandidate> candidates)
    {
        foreach (var c in candidates)
        {
            var s = c.Source; var field = s.Fields[c.Column]; s.CaptureOriginal();
            var prior = field.MissingTokens?.ToArray();
            var tokens = new HashSet<string>(s.EffectiveMissingTokens(c.Column), StringComparer.Ordinal);
            bool addCodes = c.Codes.Length > 0;
            if (addCodes) { tokens.UnionWith(c.Codes); field.MissingTokens = new(tokens, StringComparer.Ordinal); }
            s.Steps.Add(new(StepKind.Convert, c.Column, Target: ValueKind.Number, MissingTokens: tokens.ToArray(), RestoreMissing: addCodes, PriorFieldMissing: prior));
            foreach (var row in s.Rows) row[c.Column] = Values.ConvertWithMissing(row[c.Column], ValueKind.Number, "yyyy-MM-dd", tokens);
            field.Kind = ValueKind.Number;
        }
    }
    /// <summary>Undoes every type conversion of one variable: values return to their recorded text and the type to Text.</summary>
    public static void Revert(Source source, int column)
    {
        var converts = source.Steps.Where(st => st.Kind == StepKind.Convert && st.Column == column).ToList();
        if (converts.Count == 0) throw new InvalidOperationException("This variable has no recorded type change to revert.");
        var trial = source.Copy(); trial.Steps.RemoveAll(st => st.Kind == StepKind.Convert && st.Column == column);
        List<string[]> replay;
        try { replay = Recipe.Replay(trial); }
        catch (InvalidOperationException) { throw new InvalidOperationException("Later corrections to this variable depend on its current type. Undo those corrections first, or use Undo."); }
        var restore = converts.FirstOrDefault(st => st.RestoreMissing);
        source.Steps.RemoveAll(st => st.Kind == StepKind.Convert && st.Column == column);
        if (restore != null) source.Fields[column].MissingTokens = restore.PriorFieldMissing == null ? null : new(restore.PriorFieldMissing, StringComparer.Ordinal);
        for (int r = 0; r < source.Rows.Count; r++) source.Rows[r][column] = replay[r][column];
        source.Fields[column].Kind = ValueKind.Text;
    }
}

public sealed record ConversionExample(string Variable, int Record, string Before, string After, bool Missing, bool Invalid);
public sealed record ConversionPreview(int Total, int Changed, int Missing, int NewlyMissing, int LeadingZeroLoss, int Invalid, IReadOnlyList<ConversionExample> Examples);

public static class Engine
{
    private sealed record Variable(string Key, string Name, ValueKind Kind, string Source, bool Timed, List<string>? WideTimes = null, bool EntityBound = false, List<string>? EntityValues = null);
    private sealed class Record
    {
        public string Id = "";
        public string Entity = "";
        public string Time = "";
        public Dictionary<string, Cell> Values = new(StringComparer.Ordinal);
    }

    private static string Key(Source s, int column) => s.Key + ":" + column;
    private static string GroupKey(Source s, Field f) => s.Key + ":measure:" + f.Measure;

    public static View SourceView(Source source)
    {
        var view = new View();
        var columns = source.Fields.Select((f, i) => (f, i)).Where(x => x.f.Keep || source.IsKeyColumn(x.i)).ToList();
        view.Fields = columns.Select(x => new OutputField(x.f.Name, x.f.Kind, source.Name, Key(source, x.i))).ToList();
        for (int r = 0; r < source.Rows.Count; r++) if (source.Keeps(r)) view.Rows.Add(columns.Select(x => SourceCell(source, r, x.i)).ToArray());
        return view;
    }
    private static Cell SourceCell(Source source, int row, int column) => new(source.Rows[row][column], new(source.Key, row, column), source.IsMissing(source.Rows[row][column], column));

    public static HashSet<string> SuggestedRowVariables(Session session, bool stack = false)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in session.Sources)
            for (int c = 0; c < source.Fields.Count; c++)
            {
                var field = source.Fields[c];
                if (!field.Keep || source.IsKeyColumn(c)) continue;
                if (field.Measure.Length == 0) continue;
                result.Add(stack ? "stack:" + (field.Measure.Length > 0 ? field.Measure : field.Name) : field.Measure.Length > 0 ? GroupKey(source, field) : Key(source, c));
            }
        return result;
    }

    public static View Build(Session session, Layout layout, MatchMode match = MatchMode.AllPeople, bool stack = false, IEnumerable<string>? rowVariables = null)
    {
        if (session.Sources.Count == 0) return new();
        var variables = new List<Variable>();
        var sourceRecords = new List<List<Record>>();
        foreach (var source in session.Sources)
        {
            if (source.IdColumn < 0 || source.IdColumn >= source.Fields.Count) throw new InvalidOperationException($"Choose an ID column for {source.Name} in Source settings.");
            var configuredKeys = new[] { source.IdColumn, source.EntityColumn, source.TimeColumn }.Where(c => c >= 0).ToArray();
            if (source.EntityColumn < -1 || source.TimeColumn < -1 || configuredKeys.Any(c => c >= source.Fields.Count) || configuredKeys.Distinct().Count() != configuredKeys.Length) throw new InvalidOperationException($"ID, additional key, and time/grouping columns must be different valid columns in {source.Name}.");
            bool hasWide = source.Fields.Where((f, c) => !source.IsKeyColumn(c)).Any(f => f.Keep && f.Measure.Length > 0);
            if (hasWide && source.TimeColumn >= 0) throw new InvalidOperationException($"{source.Name} has a time column and repeated-column mappings. Clear one mapping before combining; nested repeated measures need an explicit plan.");
            var sourceEntities = source.EntityColumn < 0 ? null : source.Rows.Where((_, r) => source.Keeps(r)).Select(r => r[source.EntityColumn]).Where(entity => !source.IsMissing(entity, source.EntityColumn) && !string.IsNullOrWhiteSpace(entity)).Distinct(StringComparer.Ordinal).ToList();
            var perSource = new List<Variable>();
            for (int c = 0; c < source.Fields.Count; c++)
            {
                var f = source.Fields[c];
                if (!f.Keep || source.IsKeyColumn(c)) continue;
                bool repeated = f.Measure.Length > 0;
                if (repeated != (f.Occasion.Length > 0)) throw new InvalidOperationException($"Set both measure and time label, or clear both, for {source.Name} / {f.Name}.");
                string key = repeated ? GroupKey(source, f) : Key(source, c);
                if (perSource.Any(v => v.Key == key && v.Kind != f.Kind)) throw new InvalidOperationException($"Repeated measure {f.Measure} has inconsistent types in {source.Name}.");
                var existing = perSource.FirstOrDefault(v => v.Key == key);
                if (existing == null) perSource.Add(new(key, repeated ? f.Measure : f.Name, f.Kind, source.Name, repeated || source.TimeColumn >= 0, repeated ? [f.Occasion] : null, source.EntityColumn >= 0, sourceEntities));
                else if (repeated && !existing.WideTimes!.Contains(f.Occasion, StringComparer.Ordinal)) existing.WideTimes!.Add(f.Occasion);
            }
            variables.AddRange(perSource);
            var records = new List<Record>();
            var seen = new HashSet<(string, string, string)>();
            for (int r = 0; r < source.Rows.Count; r++)
            {
                if (!source.Keeps(r)) continue;
                string id = source.Rows[r][source.IdColumn];
                if (source.IsMissing(id, source.IdColumn) || string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException($"{source.Name}, record {r + 2}: ID is missing.");
                string entity = source.EntityColumn < 0 ? "" : source.Rows[r][source.EntityColumn];
                if (source.EntityColumn >= 0 && (source.IsMissing(entity, source.EntityColumn) || string.IsNullOrWhiteSpace(entity))) throw new InvalidOperationException($"{source.Name}, record {r + 2}: additional key '{source.Fields[source.EntityColumn].Name}' is missing. Resolve it before merging.");
                string time = source.TimeColumn < 0 ? "" : source.Rows[r][source.TimeColumn];
                if (source.TimeColumn >= 0 && (source.IsMissing(time, source.TimeColumn) || string.IsNullOrWhiteSpace(time))) throw new InvalidOperationException($"{source.Name}, record {r + 2}: grouping value is missing. Resolve it before merging.");
                if (!seen.Add((id, entity, time))) throw new InvalidOperationException($"{source.Name}: duplicate ID/additional-key/time keys (record {r + 2}). Choose the correct key columns or resolve duplicate records. Several people sharing an ID and time need an additional key such as person or role. No rows were multiplied or discarded.");
                var basic = new Record { Id = id, Entity = entity, Time = time };
                var timed = new Dictionary<string, Record>(StringComparer.Ordinal);
                for (int c = 0; c < source.Fields.Count; c++)
                {
                    var f = source.Fields[c];
                    if (!f.Keep || source.IsKeyColumn(c)) continue;
                    var cell = SourceCell(source, r, c);
                    if (f.Measure.Length == 0) basic.Values.Add(Key(source, c), cell);
                    else
                    {
                        if (!timed.TryGetValue(f.Occasion, out var record)) timed[f.Occasion] = record = new Record { Id = id, Entity = entity, Time = f.Occasion };
                        if (!record.Values.TryAdd(GroupKey(source, f), cell)) throw new InvalidOperationException($"Two columns map to {f.Measure} at {f.Occasion} in {source.Name}. Change the repeated-measure mapping.");
                    }
                }
                records.Add(basic); records.AddRange(timed.Values);
            }
            sourceRecords.Add(records);
        }
        var selectedRows = rowVariables?.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        if (stack) return Stack(session, sourceRecords, variables, layout, selectedRows);

        var idSets = sourceRecords.Select(rs => rs.Select(r => r.Id).ToHashSet(StringComparer.Ordinal)).ToList();
        var ids = sourceRecords.SelectMany(rs => rs.Select(r => r.Id)).Distinct(StringComparer.Ordinal).ToList();
        if (match == MatchMode.CommonPeople) ids = ids.Where(id => idSets.All(set => set.Contains(id))).ToList();
        if (match == MatchMode.FirstFilePeople) ids = ids.Where(id => idSets[0].Contains(id)).ToList();
        var indexes = sourceRecords.Select(rs => rs.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal)).ToList();
        var merged = new List<Record>();
        foreach (string id in ids)
        {
            var all = indexes.SelectMany(index => index.TryGetValue(id, out var rs) ? rs : []).ToList();
            merged.AddRange(MergeIdRecords(id, all));
        }
        var view = Render(merged, variables, layout, selectedRows, SchemaNaming.IdName(session), SchemaNaming.GroupName(session), session.HasEntity ? SchemaNaming.EntityName(session) : null, KeyKinds(session));
        int missingPeople = ids.Count(id => idSets.Any(set => !set.Contains(id)));
        int allPeople = idSets.SelectMany(s => s).Distinct(StringComparer.Ordinal).Count();
        view.Notes.Add($"{ids.Count:N0} IDs included · {allPeople - ids.Count:N0} excluded by matching rule · {missingPeople:N0} missing from at least one file");
        view.Notes.Add("Key and time labels match exactly. ID-only values apply to all additional-key entities; entity-specific values stay with that entity.");
        AddKeyKindWarning(session, view);
        return view;
    }

    private static List<Record> MergeIdRecords(string id, List<Record> all)
    {
        var result = new List<Record>();
        var entities = all.Where(r => r.Entity.Length > 0).Select(r => r.Entity).Distinct(StringComparer.Ordinal).ToList();
        if (entities.Count == 0) entities.Add("");
        foreach (string entity in entities)
        {
            var scoped = all.Where(r => r.Entity.Length == 0 || r.Entity == entity).ToList();
            var times = scoped.Where(r => r.Time.Length > 0).Select(r => r.Time).Distinct(StringComparer.Ordinal).ToList();
            if (times.Count == 0) times.Add("");
            foreach (string time in times)
            {
                var row = new Record { Id = id, Entity = entity, Time = time };
                foreach (var item in scoped.Where(r => r.Time.Length == 0 || r.Time == time))
                    foreach (var pair in item.Values)
                        if (!row.Values.TryAdd(pair.Key, pair.Value)) throw new InvalidOperationException("Multiple source values map to the same ID/additional-key/time cell. Resolve the key or repeated-column mapping before combining.");
                result.Add(row);
            }
        }
        return result;
    }

    private static View Stack(Session session, List<List<Record>> sources, List<Variable> variables, Layout layout, HashSet<string> selectedRows)
    {
        if (layout == Layout.Person) throw new InvalidOperationException("Stacking keeps independent source records. Use ID + grouping or Custom variables, or switch to Match files for one row per ID.");
        // Match by schema name, never by column position, and reject incompatible types.
        var unified = new List<Variable>();
        foreach (var v in variables)
        {
            var existing = unified.FirstOrDefault(u => u.Name == v.Name);
            if (existing != null && existing.Kind != v.Kind) throw new InvalidOperationException($"Cannot stack {v.Name}: types differ across files.");
            if (existing == null) unified.Add(new("stack:" + v.Name, v.Name, v.Kind, "stacked files", v.Timed, v.WideTimes?.ToList(), v.EntityBound, v.EntityValues?.ToList()));
            else { int index = unified.IndexOf(existing); unified[index] = existing with { Timed = existing.Timed || v.Timed, EntityBound = existing.EntityBound || v.EntityBound, EntityValues = (existing.EntityValues ?? []).Concat(v.EntityValues ?? []).Distinct(StringComparer.Ordinal).ToList(), WideTimes = existing.WideTimes == null || v.WideTimes == null ? null : existing.WideTimes.Concat(v.WideTimes).Distinct(StringComparer.Ordinal).ToList() }; }
        }
        var rows = new List<Record>();
        string sourceKey = "\u001fsource_file";
        while (unified.Any(v => v.Key == sourceKey)) sourceKey += "\u001f";
        for (int i = 0; i < sources.Count; i++)
        {
            var map = variables.Where(v => v.Source == session.Sources[i].Name).ToDictionary(v => v.Key, v => "stack:" + v.Name);
            foreach (var group in sources[i].GroupBy(r => r.Id))
            {
                foreach (var item in MergeIdRecords(group.Key, group.ToList()))
                {
                    var row = new Record { Id = item.Id, Entity = item.Entity, Time = item.Time };
                    foreach (var pair in item.Values)
                        if (!row.Values.TryAdd(map[pair.Key], pair.Value)) throw new InvalidOperationException("Stacking needs distinct variable names within each file.");
                    row.Values[sourceKey] = new(session.Sources[i].Name);
                    rows.Add(row);
                }
            }
        }
        unified.Insert(0, new(sourceKey, "source_file", ValueKind.Text, "provenance", false));
        var view = Render(rows, unified, layout, selectedRows, SchemaNaming.IdName(session), SchemaNaming.GroupName(session), session.HasEntity ? SchemaNaming.EntityName(session) : null, KeyKinds(session));
        view.Notes.Add("Stacked by variable name. Source records remain separate, including overlapping IDs.");
        AddKeyKindWarning(session, view);
        return view;
    }

    public sealed record KeyKindSet(ValueKind Id, ValueKind Entity, ValueKind Time);

    /// <summary>Output key columns follow their source columns' type when every file agrees; otherwise they stay Text.</summary>
    public static KeyKindSet KeyKinds(Session session)
    {
        static ValueKind Common(IEnumerable<ValueKind> kinds) { var distinct = kinds.Distinct().ToList(); return distinct.Count == 1 ? distinct[0] : ValueKind.Text; }
        var valid = session.Sources.Where(s => s.IdColumn >= 0 && s.IdColumn < s.Fields.Count).ToList();
        var id = Common(valid.Select(s => s.Fields[s.IdColumn].Kind));
        var entity = Common(valid.Where(s => s.EntityColumn >= 0 && s.EntityColumn < s.Fields.Count).Select(s => s.Fields[s.EntityColumn].Kind));
        var time = Common(valid.Where(s => s.TimeColumn >= 0 && s.TimeColumn < s.Fields.Count).Select(s => s.Fields[s.TimeColumn].Kind));
        // Grouping labels taken from repeated column names must also be valid canonical values of that type.
        if (time != ValueKind.Text)
        {
            var labels = valid.SelectMany(s => s.Fields.Where((f, c) => f.Keep && f.Measure.Length > 0 && !s.IsKeyColumn(c)).Select(f => f.Occasion));
            foreach (string label in labels)
            {
                string canonical;
                try { canonical = Values.Convert(label, time); } catch (InvalidOperationException) { time = ValueKind.Text; break; }
                if (canonical != label) { time = ValueKind.Text; break; }
            }
        }
        return new(id, entity, time);
    }
    public static string? KeyKindConflict(Session session)
    {
        var valid = session.Sources.Where(s => s.IdColumn >= 0 && s.IdColumn < s.Fields.Count).ToList();
        var kinds = valid.Select(s => s.Fields[s.IdColumn].Kind).Distinct().ToList();
        if (kinds.Count < 2) return null;
        return "ID types differ across files (" + string.Join(", ", valid.Select(s => s.Name + ": " + s.Fields[s.IdColumn].Kind)) + "). The combined ID stays Text. Right-click the ID column header → Change variable type to convert every file together.";
    }
    // The combined key silently stays Text when files disagree; KeyKindConflict remains available for dialogs and checks.
    private static void AddKeyKindWarning(Session session, View view) { }

    private static View Render(List<Record> records, List<Variable> variables, Layout layout, HashSet<string> selectedRows, string idName, string groupName, string? entityName, KeyKindSet? keyKinds = null)
    {
        keyKinds ??= new(ValueKind.Text, ValueKind.Text, ValueKind.Text);
        // Keep variables adjacent by source; qualify only collisions and reserve structural names.
        var used = new HashSet<string>(["person_id", "time", "variable", "value", "source"], StringComparer.OrdinalIgnoreCase);
        used.Add(idName); used.Add(groupName);
        if (entityName != null) used.Add(entityName);
        var labels = new Dictionary<string, string>();
        foreach (var v in variables)
        {
            string name = v.Name;
            if (used.Contains(name) || variables.Count(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) > 1) name = Path.GetFileNameWithoutExtension(v.Source) + "." + name;
            string original = name; int suffix = 2;
            while (!used.Add(name)) name = original + "_" + suffix++;
            labels[v.Key] = name;
        }
        var view = new View();
        view.Fields.Add(new(idName, keyKinds.Id, "ID", "@person_id"));
        if (entityName != null && layout != Layout.Person) view.Fields.Add(new(entityName, keyKinds.Entity, "additional key", "@entity"));
        Cell[] Keys(Record row) => entityName == null ? [new(row.Id), new(row.Time)] : [new(row.Id), new(row.Entity), new(row.Time)];
        if (layout == Layout.Measurement)
        {
            var stacked = variables.Where(v => selectedRows.Contains(v.Key)).ToList();
            var context = variables.Where(v => !selectedRows.Contains(v.Key)).ToList();
            if (stacked.Count == 0) throw new InvalidOperationException("Choose variables to stack into rows. Other variables will stay in their own columns.");
            view.Fields.Add(new(groupName, keyKinds.Time, "grouping", "@time"));
            view.Fields.AddRange(context.Select(v => new OutputField(labels[v.Key], v.Kind, v.Source, v.Key)));
            var valueKinds = stacked.Select(v => v.Kind).Distinct().ToList();
            var valueKind = valueKinds.Count == 1 ? valueKinds[0] : ValueKind.Text;
            var structuralNames = view.Fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string Structural(string name) { string candidate = name; int suffix = 2; while (!structuralNames.Add(candidate)) candidate = name + "_" + suffix++; return candidate; }
            view.Fields.AddRange([new(Structural("variable"), ValueKind.Text, "stacked variable", "@variable"), new(Structural("value"), valueKind, "selected values", "@value"), new(Structural("source"), ValueKind.Text, "provenance", "@source")]);
            var names = stacked.Select(v => new Cell(labels[v.Key])).ToArray(); var sources = stacked.Select(v => new Cell(v.Source)).ToArray();
            foreach (var row in records)
            {
                // Context values are the same for every stacked variable of a record; look them up once.
                var prefix = Keys(row).Concat(context.Select(v => row.Values.TryGetValue(v.Key, out var value) ? value : Cell.Empty)).ToArray();
                for (int i = 0; i < stacked.Count; i++)
                {
                    if (!row.Values.TryGetValue(stacked[i].Key, out var cell)) continue;
                    var cells = new Cell[prefix.Length + 3]; prefix.CopyTo(cells, 0);
                    cells[^3] = names[i]; cells[^2] = cell; cells[^1] = sources[i];
                    view.Rows.Add(cells);
                }
            }
        }
        else if (layout == Layout.Person)
        {
            var times = records.Select(r => r.Time).Where(t => t.Length > 0).Concat(variables.Where(v => v.WideTimes != null).SelectMany(v => v.WideTimes!)).Distinct(StringComparer.Ordinal).ToList();
            var entities = records.Select(r => r.Entity).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            if (entities.Count == 0) entities.Add("");
            var columns = new List<(Variable V, string? Entity, string? Time)>();
            var outputNames = new HashSet<string>([idName], StringComparer.OrdinalIgnoreCase);
            foreach (var v in variables)
            {
                var entityValues = v.EntityBound ? (v.EntityValues is { Count: > 0 } ? v.EntityValues : entities).Cast<string?>().ToList() : new List<string?> { null };
                foreach (var entity in entityValues)
                {
                    var entityTimes = entity == null ? times : records.Where(r => r.Entity.Length == 0 || r.Entity == entity).Select(r => r.Time).Where(t => t.Length > 0).Concat(v.WideTimes ?? []).Distinct(StringComparer.Ordinal).ToList();
                    var occasions = v.Timed ? entityTimes.Where(t => v.WideTimes == null || v.WideTimes.Contains(t, StringComparer.Ordinal)).Cast<string?>().ToList() : new List<string?> { null };
                    foreach (var time in occasions)
                    {
                        string name = labels[v.Key] + (entity == null || entity.Length == 0 ? "" : "__" + entity) + (time == null ? "" : "__" + time);
                        if (!outputNames.Add(name)) throw new InvalidOperationException("Wide output names collide. Rename the variables, additional-key labels, or time labels.");
                        string key = v.Key + (entity == null ? "" : "\u001fentity:" + entity) + (time == null ? "" : "\u001ftime:" + time);
                        view.Fields.Add(new(name, v.Kind, v.Source, key)); columns.Add((v, entity, time));
                    }
                }
            }
            foreach (var group in records.GroupBy(r => r.Id))
            {
                // Index this ID's values by variable once instead of scanning every record for every output column.
                var byVariable = new Dictionary<string, List<(Record Record, Cell Cell)>>(StringComparer.Ordinal);
                foreach (var record in group)
                    foreach (var pair in record.Values)
                    {
                        if (!byVariable.TryGetValue(pair.Key, out var found)) byVariable[pair.Key] = found = [];
                        found.Add((record, pair.Value));
                    }
                var row = new Cell[columns.Count + 1]; row[0] = new(group.Key);
                for (int i = 0; i < columns.Count; i++)
                {
                    var (variable, entity, time) = columns[i];
                    Cell? chosen = null;
                    if (byVariable.TryGetValue(variable.Key, out var candidates))
                        foreach (var (record, cell) in candidates)
                        {
                            if ((entity != null && record.Entity != entity) || (time != null && record.Time != time)) continue;
                            if (chosen == null) chosen = cell;
                            else if (chosen.Value != cell.Value || chosen.Missing != cell.Missing) throw new InvalidOperationException("A variable changes within the same wide output cell. Choose the correct additional key/time columns before widening.");
                        }
                    row[i + 1] = chosen ?? Cell.Empty;
                }
                view.Rows.Add(row);
            }
        }
        else
        {
            view.Fields.Add(new(groupName, keyKinds.Time, "grouping", "@time"));
            view.Fields.AddRange(variables.Select(v => new OutputField(labels[v.Key], v.Kind, v.Source, v.Key)));
            foreach (var row in records)
                view.Rows.Add(Keys(row).Concat(variables.Select(v => row.Values.TryGetValue(v.Key, out var cell) ? cell : Cell.Empty)).ToArray());
        }
        return view;
    }
}

public static class Demo
{
    public static Session Create(int people = 600)
    {
        var demographics = new Source { Name = "Participants.csv", IdColumn = 0, IdReason = "participant_id identifies each fictional person across all three files." };
        demographics.Fields = new[] { "participant_id", "name", "age", "study_group", "city", "height_cm" }.Select(n => new Field { Original = n, Name = n, Kind = n is "age" or "height_cm" ? ValueKind.Number : ValueKind.Text }).ToList();
        var visits = new Source { Name = "Visits.csv", IdColumn = 0, TimeColumn = 1, IdReason = "Each fictional person has a Baseline, Month 3, and Month 6 visit." };
        visits.Fields = new[] { "participant_id", "visit", "visit_date", "weight_kg", "appointment_status" }.Select(n => new Field { Original = n, Name = n, Kind = n == "weight_kg" ? ValueKind.Number : n == "visit_date" ? ValueKind.Date : ValueKind.Text }).ToList();
        var assessments = new Source { Name = "Health_measurements_wide.csv", IdColumn = 0, IdReason = "One row per person; measurement columns repeat for three visits." };
        assessments.Fields.Add(new() { Original = "participant_id", Name = "participant_id" });
        string[] measures = ["systolic_bp", "diastolic_bp", "heart_rate", "temperature_c", "sleep_hours", "steps_per_day", "pain_score", "energy_score", "stress_score", "mobility_score", "grip_strength_kg", "walking_speed_mps", "oxygen_saturation_pct", "glucose_mg_dl"];
        string[] times = ["Baseline", "Month 3", "Month 6"];
        string[] suffixes = ["baseline", "month3", "month6"];
        foreach (string measure in measures) for (int t = 0; t < times.Length; t++)
            assessments.Fields.Add(new() { Original = measure + "_" + suffixes[t], Name = measure + "_" + suffixes[t], Measure = measure, Occasion = times[t], Kind = ValueKind.Number });
        string[] firstNames = ["Avery", "Jordan", "Sam", "Taylor", "Riley", "Morgan", "Casey", "Alex", "Jamie", "Cameron", "Quinn", "Drew"];
        string[] lastNames = ["Reed", "Chen", "Rivera", "Patel", "Brooks", "Kim", "Parker", "Garcia", "Lee", "Hayes", "Diaz", "Singh", "Morgan", "Bell", "Wright", "Cruz", "Lane", "Ross", "Baker", "Shaw", "Price", "Cole", "Gray", "Wood", "Fox", "Ward", "Hill", "West", "Stone", "Hart", "Reese", "Day", "Lake", "Frost", "Hale", "Green", "White", "Young", "King", "Lewis", "Clark", "Hall", "Allen", "Scott", "Adams", "Nelson", "Evans", "Turner", "Moore", "Cook"];
        for (int p = 1; p <= people; p++)
        {
            string id = p.ToString("D5", CultureInfo.InvariantCulture);
            string name = firstNames[(p - 1) % firstNames.Length] + " " + lastNames[(p - 1 + (p - 1) / firstNames.Length) % lastNames.Length];
            demographics.Rows.Add([id, name, (24 + p % 50).ToString(), p % 2 == 0 ? "Treatment" : "Control", new[] { "Portland", "Seattle", "Boston", "Denver" }[p % 4], (155 + p % 35).ToString()]);
            int t = 0;
            foreach (string time in times)
            {
                visits.Rows.Add([id, time, new DateTime(2025, 1 + t * 3, 10 + p % 15).ToString("yyyy-MM-dd"), (60 + p % 30 + t).ToString(), p % 17 == 0 && t == 1 ? "Rescheduled" : "Completed"]); t++;
            }
            var values = new List<string> { id };
            for (int m = 0; m < measures.Length; m++) for (int visit = 0; visit < 3; visit++)
            {
                decimal value = m switch
                {
                    0 => 112 + p % 25 - visit, 1 => 68 + p % 17, 2 => 58 + p % 30 + visit,
                    3 => 36.4m + p % 8 / 10m, 4 => 5.5m + p % 30 / 10m + visit / 10m,
                    5 => 3500 + p % 70 * 100 + visit * 200, 6 => (p + visit) % 8,
                    7 => 3 + (p + visit) % 8, 8 => 2 + p % 7, 9 => 60 + p % 35 + visit,
                    10 => 20 + p % 25 + visit / 2m, 11 => 0.8m + p % 8 / 10m,
                    12 => 95 + p % 5, _ => 80 + p % 35
                };
                values.Add(value.ToString(CultureInfo.InvariantCulture));
            }
            assessments.Rows.Add(values.ToArray());
        }
        foreach (var source in new[] { demographics, visits, assessments }) { source.IdChosen = true; source.GroupChosen = true; source.CaptureOriginal(); }
        return new() { Sources = [demographics, visits, assessments] };
    }
}
