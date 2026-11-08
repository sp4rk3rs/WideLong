using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WideLong;

/// <summary>Writes the cleaning as a commented SAS program: read the originals, clean each file, combine, finish.</summary>
public static class SasRecipe
{
    private static string Q(string s) => Recipe.SasQuote(s);
    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    public static string DateInformat(string format) => format switch
    {
        "yyyy-MM-dd" or "yyyy/MM/dd" => "yymmdd10.", "MM/dd/yyyy" or "MM-dd-yyyy" => "mmddyy10.",
        "dd/MM/yyyy" or "dd-MM-yyyy" => "ddmmyy10.", "yyyyMMdd" => "yymmdd8.",
        _ => throw new InvalidOperationException("SAS code currently supports yyyy-MM-dd, yyyy/MM/dd, MM/dd/yyyy, dd/MM/yyyy, and yyyyMMdd date patterns. Choose R for other supported numeric date patterns.")
    };

    /// <summary>Valid, unique SAS names (letters, digits, underscores; at most 32 characters; never starting with an underscore).</summary>
    internal static List<string> Names(IEnumerable<string> names, IEnumerable<string>? taken = null)
    {
        var used = new HashSet<string>(taken ?? [], StringComparer.OrdinalIgnoreCase); var result = new List<string>();
        foreach (string item in names)
        {
            string name = Regex.Replace(item, "[^A-Za-z0-9_]", "_");
            if (name.Length == 0 || !char.IsAsciiLetter(name[0])) name = "v" + (name.StartsWith('_') ? "" : "_") + name;
            name = name[..Math.Min(name.Length, 32)]; string root = name; int n = 2;
            while (!used.Add(name)) { string suffix = "_" + n++; name = root[..Math.Min(root.Length, 32 - suffix.Length)] + suffix; }
            result.Add(name);
        }
        return result;
    }
    private static string Lit(ValueKind kind, string value) => kind == ValueKind.Text ? Q(value) : value.Length == 0 ? "." : kind switch
    {
        ValueKind.Number => value,
        ValueKind.Date => "'" + DateTime.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("ddMMMyyyy", CultureInfo.InvariantCulture).ToUpperInvariant() + "'d",
        _ => value == "true" ? "1" : "0"
    };
    private static string Lits(ValueKind kind, IEnumerable<string> values) => "(" + string.Join(", ", values.Select(v => Lit(kind, v))) + ")";

    public static string Generate(CodePlan plan, bool portablePaths = false) => Generate(plan, portablePaths, "prepared_data");

    /// <summary>Matching columns are text while files are combined; these output columns are typed at the end.</summary>
    private static List<(string Name, ValueKind Kind)> KeyTypes(CodeModel m)
    {
        if (m.Plan.Layout != Layout.Source) return m.Raw.Fields.Where(x => x.Key is "@person_id" or "@entity" or "@time" && x.Kind != ValueKind.Text).Select(x => (x.Name, x.Kind)).ToList();
        var s = m.Files[0].Source;
        return new[] { s.IdColumn, s.EntityColumn, s.TimeColumn }.Where(c => c >= 0 && s.Fields[c].Kind != ValueKind.Text).Order().Select(c => (s.Fields[c].Name, s.Fields[c].Kind)).ToList();
    }
    /// <summary>Widest stored value (UTF-8 bytes) of a combined variable, for SAS character lengths.</summary>
    private static int Width(CodeModel m, string key)
    {
        int best = 1;
        foreach (var f in m.Files)
            for (int c = 0; c < f.Source.Fields.Count; c++)
            {
                var field = f.Source.Fields[c];
                string variable = field.Measure.Length > 0 ? (m.Plan.Stack ? "stack:" + field.Measure : f.Source.Key + ":measure:" + field.Measure) : m.VariableKey(f, c);
                if (variable == key) best = Math.Max(best, f.Widths[c]);
            }
        return best;
    }

    public static string Generate(CodePlan plan, bool portablePaths, string datasetName)
    {
        foreach (var source in plan.Session.Sources)
        {
            var allValues = source.OriginalRows!.Concat(source.Rows).SelectMany(r => r).Concat(source.Fields.Select(f => f.Original)).Concat(source.Steps.SelectMany(s => new[] { s.Before, s.After })).Concat(Enumerable.Range(0, source.Fields.Count).SelectMany(source.EffectiveMissingTokens));
            if (allValues.Any(v => v.Contains('\r') || v.Contains('\n') || v.Contains('\0'))) throw new InvalidOperationException("SAS code cannot yet read multiline/control-character CSV values. Choose R for this dataset.");
            if (allValues.Any(v => v.Length > 0 && v != v.Trim())) throw new InvalidOperationException("SAS fixed-width characters cannot reproduce leading/trailing-whitespace distinctions in these sources. Choose R for exact text handling.");
            if (allValues.Any(v => Bytes(v) > 32767)) throw new InvalidOperationException("A value exceeds SAS's 32,767-byte character limit. Choose R.");
            if (source.Fields.Any(f => Bytes(f.Original) > 256)) throw new InvalidOperationException("A source header exceeds SAS's 256-byte label limit. Choose R.");
            foreach (var step in source.Steps.Where(s => s.Kind == StepKind.Convert && s.Target == ValueKind.Date && !source.IsKeyColumn(s.Column))) _ = DateInformat(step.DateFormat);
        }
        if (plan.Filter.Contains('\r') || plan.Filter.Contains('\n')) throw new InvalidOperationException("Choose R for a multiline row filter.");
        if (plan.Columns.Any(c => Bytes(c.Field.Name) > 256)) throw new InvalidOperationException("An output name exceeds SAS's 256-byte label limit. Choose R.");
        var m = CodeModel.Build(plan);
        Filtering.EnsureSasCompatible(m.Raw, plan.StructuredFilter);

        var body = new StringBuilder();
        void L(string line = "") => body.Append(CodeModel.Wrap(line, true)).Append('\n');
        string outName = Names([datasetName])[0];
        var ds = m.ObjectNames(26, ["rows", "combined", "wide", "stacked", "keys", outName]);
        string Ds(FilePlan f) => ds[m.Files.IndexOf(f)];
        var fin = new Dictionary<FilePlan, List<string>>();

        // ---- 1. Read ----
        string? folder = m.CommonFolder(portablePaths);
        L("/* ---- 1. Read the original files (every column as text, so codes and leading zeros survive) ---- */");
        L(folder != null ? $"%let folder = {folder};  /* where the original files are */" : portablePaths || m.Files.All(f => f.Source.ImportPath == null) ? "%let folder = C:\\path\\to\\files;  /* edit: the folder holding the original files */" : "");
        bool useFolder = folder != null || portablePaths || m.Files.All(f => f.Source.ImportPath == null);
        foreach (var f in m.Files)
        {
            var s = f.Source; var raw = Names(s.Fields.Select(x => x.Original));
            string file = useFolder ? $"\"&folder\\{s.Name}\"" : Q(s.ImportPath ?? s.Name);
            string encoding = s.EncodingName switch { "windows-1252" => "wlatin1", "UTF-16LE" => "utf-16le", _ => "utf-8" };
            string dlm = s.Delimiter switch { ',' => "", '\t' => " dlm='09'x", _ => $" dlm={Q(s.Delimiter.ToString())}" };
            long lrecl = Math.Max(32767, s.OriginalRows!.Select(r => r.Sum(v => (long)Bytes(Csv.Escape(v))) + r.Length + 1024).DefaultIfEmpty(1).Max() * 2);
            L($"data {Ds(f)};");
            L($"  infile {file} dsd{dlm} firstobs=2 truncover lrecl={lrecl} encoding={Q(encoding)};");
            L("  length " + string.Join(" ", raw.Select((n, c) => $"{n} ${f.Widths[c]}")) + ";");
            L("  input @; if _infile_ = '' then delete;  /* skip blank lines */");
            L("  input " + string.Join(" ", raw) + ";");
            var readLabels = raw.Select((n, c) => (n, s.Fields[c].Original)).Where(x => x.n != x.Original).ToList();
            if (readLabels.Count > 0) L("  label " + string.Join(" ", readLabels.Select(x => $"{x.n} = {Q(x.Original)}")) + ";");
            L("run;");
        }

        // ---- 2. Clean each file ----
        if (m.Files.Any(f => f.Ops.Count > 0 || f.Kinds.Any(k => k != ValueKind.Text))) { L(); L("/* ---- 2. Clean each file ---- */"); }
        foreach (var f in m.Files)
        {
            var s = f.Source; int n = s.Fields.Count;
            var raw = Names(s.Fields.Select(x => x.Original));
            var final = Names(s.Fields.Select(x => x.Name)); fin[f] = final;
            var cur = raw.ToArray(); var kind = new ValueKind[n];
            var pdv = new HashSet<string>(raw, StringComparer.OrdinalIgnoreCase);
            var helpers = new List<string>(); int arrays = 0;
            string Fresh(string name) { string root = name[..Math.Min(name.Length, 30)] + "_n"; string candidate = root; int k = 2; while (pdv.Contains(candidate)) { string suffix = "_" + k++; candidate = name[..Math.Min(name.Length, 32 - 2 - suffix.Length)] + "_n" + suffix; } pdv.Add(candidate); return candidate; }
            bool needsStep = f.Ops.Count > 0 || Enumerable.Range(0, n).Any(c => raw[c] != final[c]);
            if (!needsStep) continue;
            L(); L($"/* {s.Name} */");
            L($"data {Ds(f)};");
            L($"  set {Ds(f)};");
            Type? previous = null;
            foreach (var op in f.Ops)
            {
                bool first = op.GetType() != previous; previous = op.GetType();
                switch (op)
                {
                    case RenameOp:
                        break; // applied with the RENAME statement at the end of this step
                    case CellOp cell:
                        if (first) L("  /* Correct single values (row = data row, not counting the header) */");
                        L($"  if _n_ = {cell.Row + 1} then {cur[cell.Col]} = {Lit(cell.Kind, cell.After)};  /* was {CodeModel.Show(cell.Before)} */");
                        break;
                    case ReplaceOp replace:
                    {
                        if (first) L("  /* Recode values */");
                        string x = cur[replace.Col];
                        string condition = replace.Before.Length == 0 ? $"missing({x})" : $"{x} = {Lit(replace.Kind, replace.Before)}";
                        L($"  if {condition} then {x} = {Lit(replace.Kind, replace.After)};");
                        break;
                    }
                    case ConvertOp convert:
                    {
                        if (convert.From != ValueKind.Text && convert.Target != ValueKind.Text && convert.From != convert.Target)
                            throw new InvalidOperationException("SAS code cannot yet chain type changes between Number, Date and Boolean. Choose R, or revert the variable type and convert it once.");
                        string what = convert.Target switch
                        {
                            ValueKind.Number => "Store as numbers", ValueKind.Date => $"Store as dates (written as {convert.DateFormat})",
                            ValueKind.Boolean => "Store as 1/0 (true/yes/1 = 1, false/no/0 = 0)", _ => "Store as text"
                        };
                        L($"  /* {what}{(convert.Tokens.Length == 0 ? "" : $"; {CodeModel.ShowList(convert.Tokens)} count{(convert.Tokens.Length == 1 ? "s" : "")} as missing")} */");
                        if (convert.From == convert.Target)
                        {
                            foreach (int c in convert.Cols) L($"  if {cur[c]} in {Lits(convert.From, convert.Tokens)} then {cur[c]} = {(convert.From == ValueKind.Text ? "''" : ".")};");
                            break;
                        }
                        var olds = convert.Cols.Select(c => cur[c]).ToList();
                        var news = convert.Cols.Select(c => Fresh(final[c])).ToList();
                        string Assign(string x, string t) => convert.Target switch
                        {
                            ValueKind.Number => convert.From == ValueKind.Text ? $"{t} = input({x}, ?? best32.);" : "",
                            ValueKind.Date => $"{t} = input({x}, ?? {DateInformat(convert.DateFormat)});",
                            ValueKind.Boolean => $"if lowcase({x}) in ('true', 'yes', '1') then {t} = 1; else if lowcase({x}) in ('false', 'no', '0') then {t} = 0;",
                            _ => convert.From switch
                            {
                                ValueKind.Number => $"if not missing({x}) then {t} = strip(put({x}, best32.));",
                                ValueKind.Date => $"if not missing({x}) then {t} = put({x}, yymmdd10.);",
                                _ => $"if {x} = 1 then {t} = 'true'; else if {x} = 0 then {t} = 'false';"
                            }
                        };
                        string Tokens(string x) => convert.Tokens.Length == 0 ? "" : $"if {x} in {Lits(ValueKind.Text, convert.Tokens)} then {x} = ''; ";
                        if (convert.Target == ValueKind.Text) L("  length " + string.Join(" ", news.Select(t => t + " $32")) + ";");
                        if (convert.Cols.Count >= 4)
                        {
                            arrays++; string from = $"_from{arrays}", to = $"_to{arrays}";
                            L($"  array {from}{{*}} {(convert.From == ValueKind.Text ? "$ " : "")}{string.Join(" ", olds)};");
                            L($"  array {to}{{*}} {(convert.Target == ValueKind.Text ? "$ " : "")}{string.Join(" ", news)};");
                            L($"  do _i = 1 to dim({from}); {Tokens(from + "{_i}")}{Assign(from + "{_i}", to + "{_i}")} end;");
                            if (!helpers.Contains("_i")) helpers.Add("_i");
                        }
                        else for (int i = 0; i < olds.Count; i++) L("  " + Tokens(olds[i]) + Assign(olds[i], news[i]));
                        if (convert.Target == ValueKind.Date) L("  format " + string.Join(" ", news) + " yymmdd10.;");
                        for (int i = 0; i < convert.Cols.Count; i++) cur[convert.Cols[i]] = news[i];
                        break;
                    }
                    case MissingOp missing:
                    {
                        var tokens = missing.Kind == ValueKind.Text ? missing.Tokens.Where(t => t.Length > 0).ToArray() : missing.Tokens;
                        if (tokens.Length == 0) break;
                        L("  /* Missing-value codes become missing */");
                        string blank = missing.Kind == ValueKind.Text ? "''" : ".";
                        if (missing.Cols.Count >= 4)
                        {
                            arrays++; string a = $"_miss{arrays}";
                            L($"  array {a}{{*}} {(missing.Kind == ValueKind.Text ? "$ " : "")}{string.Join(" ", missing.Cols.Select(c => cur[c]))};");
                            L($"  do _i = 1 to dim({a}); if {a}{{_i}} in {Lits(missing.Kind, tokens)} then {a}{{_i}} = {blank}; end;");
                            if (!helpers.Contains("_i")) helpers.Add("_i");
                        }
                        else foreach (int c in missing.Cols) L($"  if {cur[c]} in {Lits(missing.Kind, tokens)} then {cur[c]} = {blank};");
                        break;
                    }
                    case KeepOp keep:
                    {
                        L("  /* Filter responses: keep only the checked answers */");
                        var parts = keep.Rules.Select(r =>
                        {
                            var values = r.Kind == ValueKind.Text ? r.Values.Where(v => v.Length > 0).ToArray() : r.Values;
                            string inSet = values.Length == 0 ? "0" : $"{cur[r.Col]} in {Lits(r.Kind, values)}";
                            return r.KeepMissing ? $"({inSet} or missing({cur[r.Col]}))" : inSet;
                        }).ToList();
                        var removed = keep.Rules.SelectMany(r => r.Removed.Select(CodeModel.Show).Concat(r.RemovesMissing ? ["missing"] : [])).ToList();
                        L($"  if not ({string.Join(" and ", parts)}) then delete;" + (removed.Count == 0 ? "" : "  /* leaves out " + string.Join(", ", removed.Take(6)) + (removed.Count > 6 ? ", …" : "") + " */"));
                        break;
                    }
                    case SelectOp:
                        break; // KEEP/DROP below
                }
            }
            var kept = Enumerable.Range(0, n).Where(c => s.Fields[c].Keep || s.IsKeyColumn(c)).ToList();
            var keepNames = kept.Select(c => cur[c]).ToList();
            var dropNames = pdv.Where(x => !keepNames.Contains(x, StringComparer.OrdinalIgnoreCase)).Concat(helpers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (dropNames.Count > 0)
            {
                if (f.Ops.OfType<SelectOp>().Any()) L("  /* Keep the selected variables */");
                L(dropNames.Count <= keepNames.Count ? "  drop " + string.Join(" ", dropNames) + ";" : "  keep " + string.Join(" ", keepNames) + ";");
            }
            var renames = kept.Where(c => cur[c] != final[c]).Select(c => $"{cur[c]} = {final[c]}").ToList();
            if (renames.Count > 0) L("  rename " + string.Join(" ", renames) + ";");
            var cleanLabels = kept.Where(c => final[c] != s.Fields[c].Name).Select(c => $"{cur[c]} = {Q(s.Fields[c].Name)}").ToList();
            if (cleanLabels.Count > 0) L("  label " + string.Join(" ", cleanLabels) + ";");
            L("run;");
        }
        foreach (var f in m.Files.Where(f => !fin.ContainsKey(f))) fin[f] = Names(f.Source.Fields.Select(x => x.Name));

        // ---- 3. Combine ----
        string id = m.IdName, g = m.GroupName, e = m.EntityName ?? "";
        var keyNames = Names(m.HasEntity ? [id, e, g] : [id, g], ["_first", "_order", "_porder"]);
        string idN = keyNames[0], eN = m.HasEntity ? keyNames[1] : "", gN = keyNames[^1];
        string Col(FilePlan f, int c) => fin[f][c];
        string current = Ds(m.Files[0]);
        var currentNames = new List<string>(); var currentRaw = new List<string>();
        string Lookup(string rawName) { int at = currentRaw.IndexOf(rawName); return at >= 0 && at < currentNames.Count ? currentNames[at] : throw new InvalidOperationException($"Internal: column '{rawName}' not found among [{string.Join(", ", currentRaw)}] / [{string.Join(", ", currentNames)}]"); }
        var helperCols = new List<string>();
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        bool measurement = m.Plan.Layout == Layout.Measurement;
        var stackedKeys = m.Plan.Preferences.RowVariables ?? new HashSet<string>(StringComparer.Ordinal);
        int keyWidth(Func<FilePlan, int> column) => Math.Max(1, m.Files.Where(f => column(f) >= 0).Select(f => f.Widths[column(f)]).DefaultIfEmpty(1).Max());
        int idW = keyWidth(f => f.Source.IdColumn), eW = keyWidth(f => f.Source.EntityColumn);
        int gW = Math.Max(keyWidth(f => f.Source.TimeColumn), m.Files.SelectMany(f => f.Occasions).Select(Bytes).DefaultIfEmpty(1).Max());
        var longNames = new Dictionary<FilePlan, (string Time, List<string> Measures)>();

        void Reshape(FilePlan f, List<string> carry)
        {
            var taken = fin[f].Concat(carry);
            string t = Names([g], taken)[0]; var measures = Names(f.Measures, taken.Append(t));
            longNames[f] = (t, measures);
            L($"/* {f.Source.Name} has one column per {g}; give each {g} its own row */");
            L($"data {Ds(f)}_long;");
            L($"  set {Ds(f)};");
            var charMeasures = f.Measures.Select((x, i) => (x, i)).Where(x => f.MeasureKind(x.x) == ValueKind.Text).Select(x => $"{measures[x.i]} ${f.Repeated.Where(r => r.Key.Measure == x.x).Max(r => f.Widths[r.Value])}");
            L("  length " + string.Join(" ", charMeasures.Prepend($"{t} ${Math.Max(1, f.Occasions.Max(Bytes))}")) + ";");
            foreach (string occasion in f.Occasions)
                L($"  {t} = {Q(occasion)}; " + string.Join(" ", f.Measures.Select((x, i) => f.Repeated.TryGetValue((x, occasion), out int c) ? $"{measures[i]} = {Col(f, c)};" : $"call missing({measures[i]});")) + " output;");
            var dates = f.Measures.Select((x, i) => (x, i)).Where(x => f.MeasureKind(x.x) == ValueKind.Date).Select(x => measures[x.i]).ToList();
            if (dates.Count > 0) L("  format " + string.Join(" ", dates) + " yymmdd10.;");
            L("  keep " + string.Join(" ", carry.Append(t).Concat(measures)) + ";");
            L("run;");
        }

        if (m.Plan.Layout == Layout.Source)
        {
            var f = m.Files[0];
            var kept = Enumerable.Range(0, f.Source.Fields.Count).Where(c => f.Source.Fields[c].Keep || f.Source.IsKeyColumn(c)).ToList();
            currentNames.AddRange(kept.Select(c => Col(f, c))); currentRaw.AddRange(kept.Select(c => f.Source.Fields[c].Name));
            current = Ds(f);
        }
        else if (m.Plan.Stack)
        {
            L(); L("/* ---- 3. Stack the files: rows stay separate, and source_file names each row's file ---- */");
            var unified = m.Plan.Variables.ToList();
            var unifiedNames = Names(unified.Select(u => u.Name), keyNames.Concat(["_first", "_order", "_porder"]));
            string sourceFile = unifiedNames[unified.FindIndex(u => u.Key.StartsWith('\u001f'))];
            var parts = new List<string>();
            foreach (var f in m.Files)
            {
                var keyCols = new List<int> { f.Source.IdColumn }; if (f.HasEntity) keyCols.Add(f.Source.EntityColumn); if (f.HasTimeColumn) keyCols.Add(f.Source.TimeColumn);
                string from = Ds(f);
                if (f.Wide) { Reshape(f, keyCols.Concat(f.Basic).Select(c => Col(f, c)).ToList()); from = Ds(f) + "_long"; }
                var renames = new List<string> { $"{Col(f, f.Source.IdColumn)}={idN}" };
                if (f.HasEntity) renames.Add($"{Col(f, f.Source.EntityColumn)}={eN}");
                if (f.HasTimeColumn) renames.Add($"{Col(f, f.Source.TimeColumn)}={gN}"); else if (f.Wide) renames.Add($"{longNames[f].Time}={gN}");
                for (int i = 0; i < unified.Count; i++)
                {
                    if (unified[i].Key.StartsWith('\u001f')) continue;
                    string variable = unified[i].Key["stack:".Length..];
                    int basic = f.Basic.FirstOrDefault(c => f.Source.Fields[c].Name == variable, -1);
                    string? have = basic >= 0 ? Col(f, basic) : f.Wide && f.Measures.IndexOf(variable) is int mi and >= 0 ? longNames[f].Measures[mi] : null;
                    if (have != null) renames.Add($"{have}={unifiedNames[i]}");
                }
                string part = Ds(f) + "_part"; parts.Add(part);
                bool reorder = f.Timed || f.HasEntity; string staged = reorder ? part + "0" : part;
                L($"data {staged};");
                L($"  set {from}(rename=({string.Join(" ", renames.Where(r => r.Split('=')[0] != r.Split('=')[1]))}));".Replace("(rename=())", ""));
                L($"  length {sourceFile} ${Math.Max(1, m.Files.Max(x => Bytes(x.Source.Name)))};");
                L($"  {sourceFile} = {Q(f.Source.Name)}; _order = _n_;");
                L("run;");
                if (reorder)
                {
                    L("/* keep each ID's rows together, in the order the IDs first appear */");
                    L("proc sql;");
                    L(f.HasEntity
                        ? $"  create table {part} as select *, min(_order) as _porder from (select *, min(_order) as _first from {staged} group by {idN}) group by {idN}, {eN} order by _first, _porder, _order;"
                        : $"  create table {part} as select *, min(_order) as _first from {staged} group by {idN} order by _first, _order;");
                    L("quit;");
                }
            }
            var charLengths = new List<string> { $"{idN} ${idW}" };
            if (m.HasEntity) charLengths.Add($"{eN} ${eW}");
            charLengths.Add($"{gN} ${gW}");
            for (int i = 0; i < unified.Count; i++)
            {
                if (unified[i].Kind != ValueKind.Text) continue;
                string variable = unified[i].Key.StartsWith('\u001f') ? "" : unified[i].Key["stack:".Length..];
                int width = variable.Length == 0 ? m.Files.Max(x => Bytes(x.Source.Name)) : m.Files.SelectMany(f => f.Basic.Where(c => f.Source.Fields[c].Name == variable).Select(c => f.Widths[c]).Concat(f.Repeated.Where(r => r.Key.Measure == variable).Select(r => f.Widths[r.Value]))).DefaultIfEmpty(1).Max();
                charLengths.Add($"{unifiedNames[i]} ${Math.Max(1, width)}");
            }
            L("data combined;");
            L("  length " + string.Join(" ", charLengths) + ";");
            L("  set " + string.Join(" ", parts) + ";");
            L("  drop _order" + (m.Files.Any(f => f.Timed || f.HasEntity) ? " _first" : "") + (m.Files.Any(f => f.HasEntity) ? " _porder" : "") + ";");
            L("run;");
            current = "combined";
            currentNames.Add(idN); currentRaw.Add(id); if (m.HasEntity) { currentNames.Add(eN); currentRaw.Add(e); } currentNames.Add(gN); currentRaw.Add(g);
            currentNames.AddRange(unifiedNames); currentRaw.AddRange(unified.Select(u => u.Name));
            for (int i = 0; i < unified.Count; i++)
            {
                if (unified[i].Key.StartsWith('\u001f')) { flags[unified[i].Key] = ""; continue; }
                string variable = unified[i].Key["stack:".Length..];
                var having = m.Files.Where(f => f.Basic.Any(c => f.Source.Fields[c].Name == variable) || f.Measures.Contains(variable)).Select(f => f.Source.Name).ToList();
                flags[unified[i].Key] = having.Count == m.Files.Count ? "" : $"{sourceFile} in {Lits(ValueKind.Text, having)}";
            }
        }
        else
        {
            L(); L("/* ---- 3. Combine the files ---- */");
            foreach (var f in m.Files.Where(f => f.Wide))
            {
                var carry = new List<string> { Col(f, f.Source.IdColumn) }; if (f.HasEntity) carry.Add(Col(f, f.Source.EntityColumn));
                Reshape(f, carry);
            }
            string Keyed(FilePlan f)
            {
                string from = f.Wide ? Ds(f) + "_long" : Ds(f);
                var keep = new List<string> { Col(f, f.Source.IdColumn) }; var renames = new List<string>();
                if (Col(f, f.Source.IdColumn) != idN) renames.Add($"{Col(f, f.Source.IdColumn)}={idN}");
                if (f.HasEntity) { keep.Add(Col(f, f.Source.EntityColumn)); if (Col(f, f.Source.EntityColumn) != eN) renames.Add($"{Col(f, f.Source.EntityColumn)}={eN}"); }
                string? t = f.HasTimeColumn ? Col(f, f.Source.TimeColumn) : f.Wide ? longNames[f].Time : null;
                if (t != null) { keep.Add(t); if (t != gN) renames.Add($"{t}={gN}"); }
                return $"{from}(keep={string.Join(" ", keep)}{(renames.Count > 0 ? " rename=(" + string.Join(" ", renames) + ")" : "")})";
            }
            L($"/* Every ID{(m.HasEntity ? ", " + e : "")} and {g} in the order they first appear */");
            L("data keys;");
            L($"  length {idN} ${idW}{(m.HasEntity ? $" {eN} ${eW}" : "")} {gN} ${gW};");
            L("  set " + string.Join(" ", m.Files.Select(Keyed)) + ";");
            L("  _order = _n_;");
            L("run;");
            string match = m.Plan.Match switch
            {
                MatchMode.CommonPeople when m.Files.Count > 1 => " where " + string.Join(" and ", m.Files.Select(f => $"{idN} in (select {Col(f, f.Source.IdColumn)} from {Ds(f)})")),
                MatchMode.FirstFilePeople when m.Files.Count > 1 => $" where {idN} in (select {Col(m.Files[0], m.Files[0].Source.IdColumn)} from {Ds(m.Files[0])})",
                _ => ""
            };
            string matchNote = m.Plan.Match == MatchMode.CommonPeople ? " (IDs found in every file)" : m.Plan.Match == MatchMode.FirstFilePeople ? " (IDs found in the first file)" : "";
            L("proc sql;");
            if (!m.HasEntity)
            {
                L($"  /* One row per ID{matchNote} and each of its {g} values; IDs without a {g} keep one row */");
                L("  create table rows as");
                L($"  select i.{idN}, k.{gN}, i._first, min(k._order) as _order");
                L($"  from (select {idN}, min(_order) as _first from keys{match} group by {idN}) as i");
                L($"       left join keys as k on k.{idN} = i.{idN} and not missing(k.{gN})");
                L($"  group by 1, 2, 3");
                L("  order by 3, 4;");
            }
            else
            {
                L($"  /* One row per ID{matchNote}, {e} and {g}. Files without a {e} apply to every {e} of that ID; files without a {g} apply to every {g} */");
                L($"  create table _ids as select {idN}, min(_order) as _first from keys{match} group by {idN};");
                L($"  create table _people as select {idN}, {eN}, min(_order) as _porder from keys where not missing({eN}) group by {idN}, {eN};");
                L("  create table rows as");
                L($"  select i.{idN}, p.{eN}, k.{gN}, i._first, coalesce(p._porder, i._first) as _porder, min(k._order) as _order");
                L("  from _ids as i");
                L($"       left join _people as p on p.{idN} = i.{idN}");
                L($"       left join keys as k on k.{idN} = i.{idN} and not missing(k.{gN}) and (missing(k.{eN}) or k.{eN} = p.{eN})");
                L("  group by 1, 2, 3, 4, 5");
                L("  order by 4, 5, 6;");
            }
            L("quit;");

            // Attach every file's variables
            var used = new HashSet<string>(keyNames.Concat(["_first", "_order", "_porder"]), StringComparer.OrdinalIgnoreCase);
            var select = new List<string> { $"r.{idN}" }; if (m.HasEntity) select.Add($"r.{eN}"); select.Add($"r.{gN}");
            currentNames.Add(idN); currentRaw.Add(id); if (m.HasEntity) { currentNames.Add(eN); currentRaw.Add(e); } currentNames.Add(gN); currentRaw.Add(g);
            var joins = new List<string>();
            foreach (var f in m.Files)
            {
                void Attach(string table, List<(string From, string Label, string Key)> vars, List<(string Mine, string Theirs)> on)
                {
                    if (vars.Count == 0) return;
                    joins.Add($"left join {table} on " + string.Join(" and ", on.Select(x => $"{table}.{x.Theirs} = r.{x.Mine}")));
                    foreach (var (from, label, key) in vars)
                    {
                        string name = Names([label], used)[0];
                        select.Add(from == name ? $"{table}.{from}" : $"{table}.{from} as {name}");
                        currentNames.Add(name); currentRaw.Add(label);
                    }
                    if (measurement && vars.Any(v => stackedKeys.Contains(v.Key)))
                    {
                        string flag = Names(["_in_" + table], used)[0];
                        select.Add($"not missing({table}.{on[0].Theirs}) as {flag}"); helperCols.Add(flag);
                        foreach (var v in vars) flags[v.Key] = flag;
                    }
                }
                var on = new List<(string, string)> { (idN, Col(f, f.Source.IdColumn)) };
                if (f.HasEntity) on.Add((eN, Col(f, f.Source.EntityColumn)));
                if (f.HasTimeColumn) on.Add((gN, Col(f, f.Source.TimeColumn)));
                Attach(Ds(f), f.Basic.Select(c => (Col(f, c), m.Label(f, c), m.VariableKey(f, c))).ToList(), on);
                if (f.Wide)
                {
                    var longOn = new List<(string, string)> { (idN, Col(f, f.Source.IdColumn)) };
                    if (f.HasEntity) longOn.Add((eN, Col(f, f.Source.EntityColumn)));
                    longOn.Add((gN, longNames[f].Time));
                    Attach(Ds(f) + "_long", f.Measures.Select((x, i) => (longNames[f].Measures[i], m.MeasureLabel(f, x), m.MeasureKey(f, x))).ToList(), longOn);
                }
            }
            select.Add("r._first"); if (m.HasEntity) select.Add("r._porder"); select.Add("r._order");
            helperCols.AddRange(m.HasEntity ? ["_first", "_porder", "_order"] : ["_first", "_order"]);
            L();
            L($"/* Add each file's variables. Values from a file without a {(m.HasEntity ? e + " or " : "")}{g} column repeat on every matching row */");
            L("proc sql;");
            L("  create table combined as");
            L("  select " + string.Join(", ", select));
            L("  from rows as r");
            foreach (string join in joins) L("       " + join);
            L($"  order by r._first, {(m.HasEntity ? "r._porder, " : "")}r._order;");
            L("quit;");
            current = "combined";
        }

        // ---- Layout ----
        if (m.Plan.Layout == Layout.Person)
        {
            L(); L($"/* ---- One row per ID: each {(m.HasEntity ? e + " and " : "")}{g} gets its own column ---- */");
            var wideNames = Names(m.Raw.Fields.Skip(1).Select(x => x.Name), [idN, "_first"]).Prepend(idN).ToList();
            var select = new List<string> { idN };
            for (int i = 1; i < m.Raw.Fields.Count; i++)
            {
                var key = m.Raw.Fields[i].Key; var parts = key.Split("\u001ftime:", 2); var entityParts = parts[0].Split("\u001fentity:", 2);
                string source = Lookup(m.Labels[entityParts[0]]);
                var conditions = new List<string>();
                if (entityParts.Length == 2 && entityParts[1].Length > 0) conditions.Add($"{eN} = {Q(entityParts[1])}");
                if (parts.Length == 2) conditions.Add($"{gN} = {Q(parts[1])}");
                select.Add(conditions.Count == 0 ? $"max({source}) as {wideNames[i]}" : $"max(case when {string.Join(" and ", conditions)} then {source} end) as {wideNames[i]}");
            }
            select.Add("min(_first) as _first");
            L("proc sql;");
            L("  create table wide as");
            L("  select " + select[0] + ",");
            for (int i = 1; i < select.Count; i++) L("         " + select[i] + (i + 1 < select.Count ? "," : ""));
            L($"  from combined group by {idN}");
            L($"  order by {select.Count};");
            L("quit;");
            current = "wide"; currentNames = wideNames; currentRaw = m.Raw.Fields.Select(x => x.Name).ToList(); helperCols = ["_first"];
        }
        else if (measurement)
        {
            var valueField = m.RawField("@value")!;
            var stacked = m.Plan.Variables.Where(v => v.Stacked).ToList();
            bool mixed = valueField.Kind == ValueKind.Text && stacked.Any(v => v.Kind != ValueKind.Text);
            var structural = Names([m.RawField("@variable")!.Name, valueField.Name, m.RawField("@source")!.Name], currentNames.Concat(helperCols));
            var context = m.Raw.Fields.TakeWhile(x => x.Key != "@variable").Select(x => Lookup(x.Name)).ToList();
            L(); L("/* ---- Put the chosen variables into variable/value rows ---- */");
            L("data stacked;");
            L("  set combined;");
            var lengths = new List<string> { $"{structural[0]} ${Math.Max(1, stacked.Max(v => Bytes(v.Name)))}", $"{structural[2]} ${Math.Max(1, stacked.Max(v => Bytes(v.Source)))}" };
            if (valueField.Kind == ValueKind.Text) lengths.Add($"{structural[1]} ${Math.Max(32, stacked.Where(v => v.Kind == ValueKind.Text).Select(v => Width(m, v.Key)).DefaultIfEmpty(1).Max())}");
            L("  length " + string.Join(" ", lengths) + ";");
            foreach (var v in stacked)
            {
                string source = Lookup(v.Name);
                string value = !mixed ? source : v.Kind switch
                {
                    ValueKind.Number => $"ifc(missing({source}), '', strip(put({source}, best32.)))", ValueKind.Date => $"ifc(missing({source}), '', put({source}, yymmdd10.))",
                    ValueKind.Boolean => $"ifc({source} = 1, 'true', ifc({source} = 0, 'false', ''))", _ => source
                };
                string flag = flags.GetValueOrDefault(v.Key, "");
                string assign = $"{structural[0]} = {Q(v.Name)}; {structural[1]} = {value}; {structural[2]} = {Q(v.Source)}; output;";
                L(flag.Length == 0 ? $"  {assign}" : $"  if {flag} then do; {assign} end;");
            }
            if (valueField.Kind == ValueKind.Date) L($"  format {structural[1]} yymmdd10.;");
            L("  keep " + string.Join(" ", context.Concat(structural)) + ";");
            L("run;");
            current = "stacked"; currentNames = context.Concat(structural).ToList(); currentRaw = m.Raw.Fields.Select(x => x.Name).ToList(); helperCols = [];
        }

        // ---- 4. Final ----
        var rawByKey = m.Raw.Fields.Select((x, i) => (x, i)).ToDictionary(x => Presentation.Identity(x.x, x.i), x => x.x.Name, StringComparer.Ordinal);
        var wanted = m.Plan.Columns.Select(c => rawByKey[c.Field.Key]).ToList();
        string Pdv(string rawName) => Lookup(rawName);
        var finalNames = Names(m.Plan.Columns.Select(c => c.Field.Name), ["_blank1", "_sort1"]);
        L(); L("/* ---- 4. Final columns, names, filters and sorting ---- */");
        L($"data {outName};");
        if (!wanted.Select(Pdv).SequenceEqual(currentNames.Where(x => wanted.Select(Pdv).Contains(x))) || KeyTypes(m).Count > 0) L("  retain " + string.Join(" ", wanted.Select(Pdv)) + ";  /* column order */");
        var keyFields = KeyTypes(m);
        var setRenames = keyFields.Select(k => (Name: Pdv(k.Name), Temp: Names(["text_" + Pdv(k.Name)], currentNames)[0], k.Kind)).ToList();
        L($"  set {current}{(setRenames.Count > 0 ? "(rename=(" + string.Join(" ", setRenames.Select(k => $"{k.Name}={k.Temp}")) + "))" : "")};");
        if (setRenames.Count > 0)
        {
            L("  /* Matching columns stayed text while combining; give them their final type */");
            foreach (var (name, temp, kind) in setRenames)
                L("  " + kind switch
                {
                    ValueKind.Number => $"{name} = input({temp}, best32.);", ValueKind.Date => $"{name} = input({temp}, yymmdd10.); format {name} yymmdd10.;",
                    _ => $"if {temp} = 'true' then {name} = 1; else if {temp} = 'false' then {name} = 0;"
                });
        }
        var compiled = Filtering.Compile(m.Raw.Fields, plan.StructuredFilter);
        if (compiled.Count > 0)
        {
            string Predicate(CompiledFilterRule c)
            {
                var rule = c.Rule; string x = Pdv(c.Field.Name);
                string Lv(string v) => Lit(rule.Kind, v);
                string comparison = rule.Operator switch
                {
                    FilterOperator.IsMissing => $"missing({x})", FilterOperator.IsNotMissing => $"not missing({x})",
                    FilterOperator.Contains => $"index({x}, {Q(rule.Value)}) > 0", FilterOperator.NotContains => $"index({x}, {Q(rule.Value)}) = 0",
                    FilterOperator.Between => $"{Lv(rule.Value)} <= {x} <= {Lv(rule.UpperValue)}",
                    _ => $"{x} {rule.Operator switch { FilterOperator.Equal => "=", FilterOperator.NotEqual => "ne", FilterOperator.Greater => ">", FilterOperator.GreaterEqual => ">=", FilterOperator.Less => "<", _ => "<=" }} {Lv(rule.Value)}"
                };
                return rule.Operator is FilterOperator.IsMissing or FilterOperator.IsNotMissing ? comparison : $"not missing({x}) and {comparison}";
            }
            var predicates = compiled.Select(Predicate).ToList();
            L($"  /* Filter rules ({(plan.StructuredFilter.MatchAll ? "all must match" : "any may match")}) */");
            L("  if " + (predicates.Count == 1 ? predicates[0] : string.Join(plan.StructuredFilter.MatchAll ? " and " : " or ", predicates.Select(x => "(" + x + ")"))) + ";");
        }
        if (plan.Filter.Length > 0)
        {
            L($"  /* Search rows: keep rows where any value contains {CodeModel.Show(plan.Filter)} (ignoring case; values as SAS formats them) */");
            L($"  if index(upcase(catx('09'x, {string.Join(", ", wanted.Select(x => $"vvalue({Pdv(x)})"))})), {Q(plan.Filter.ToUpperInvariant())}) > 0;");
        }
        var by = new List<string>();
        for (int i = 0; i < plan.Sorts.Count; i++)
        {
            var (column, descending) = plan.Sorts[i]; var field = plan.Columns[column].Field; string x = Pdv(wanted[column]);
            int rawIndex = m.Raw.Fields.FindIndex(r => r.Name == wanted[column]);
            int width = Math.Max(1, m.Raw.Rows.Select(r => Bytes(r[rawIndex].Value)).DefaultIfEmpty(1).Max());
            L($"  _blank{i + 1} = missing({x});" + (field.Kind == ValueKind.Text ? $" length _sort{i + 1} ${width}; _sort{i + 1} = upcase({x});" : ""));
            by.Add($"_blank{i + 1} " + (descending ? "descending " : "") + (field.Kind == ValueKind.Text ? $"_sort{i + 1}" : finalNames[column]));
        }
        var labels = m.Plan.Columns.Select((c, i) => (c, i)).Where(x => finalNames[x.i] != x.c.Field.Name).Select(x => $"{Pdv(wanted[x.i])} = {Q(x.c.Field.Name)}").ToList();
        if (labels.Count > 0) L("  label " + string.Join(" ", labels) + ";");
        var renames2 = wanted.Select((w, i) => (From: Pdv(w), To: finalNames[i])).Where(x => !x.From.Equals(x.To, StringComparison.Ordinal)).Select(x => $"{x.From} = {x.To}").ToList();
        if (renames2.Count > 0) L("  rename " + string.Join(" ", renames2) + ";");
        var drops = helperCols.Concat(setRenames.Select(k => k.Temp)).ToList();
        if (drops.Count > 0) L("  drop " + string.Join(" ", drops) + ";");
        L("run;");
        if (by.Count > 0)
        {
            L($"/* Sort by {string.Join(", then ", plan.Sorts.Select(s => plan.Columns[s.Column].Field.Name + (s.Descending ? " (descending)" : "")))}; blanks last, ties keep their order */");
            L($"proc sort data={outName} out={outName}(drop=_blank:{(by.Any(x => x.Contains("_sort")) ? " _sort:" : "")}); by {string.Join(" ", by)}; run;");
        }

        var b = new StringBuilder();
        b.Append($"/* {datasetName}: cleaning program written by WideLong {CodeModel.Version} on {CodeModel.Today}.\n");
        b.Append($"   Rebuilds the prepared dataset ({m.Final.Rows.Count:N0} rows x {m.Final.Fields.Count:N0} columns; {CodeModel.ShapeWord(plan.Layout)})\n");
        b.Append($"   from the original CSV files, step by step. The result is WORK.{outName.ToUpperInvariant()}.\n");
        b.Append("   SAS keeps blank text and missing text the same. True/false values are stored as 1/0. */\n");
        b.Append("options validvarname=v7;\n\n");
        b.Append(body);
        b.Append($"\n/* Save a permanent copy (edit the folder and uncomment):\n   libname out 'C:\\path\\to\\folder';\n   data out.{outName}; set {outName}; run; */\n");
        return b.ToString();
    }
}
