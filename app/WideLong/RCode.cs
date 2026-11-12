using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WideLong;

/// <summary>Writes the cleaning as a plain, commented base-R script: read the originals, clean each file, combine, finish.</summary>
internal static class RCode
{
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "if", "else", "repeat", "while", "function", "for", "next", "break", "TRUE", "FALSE", "NULL", "Inf", "NaN", "NA", "NA_integer_", "NA_real_", "NA_character_", "NA_complex_", "in" };
    public static string Name(string n) => Regex.IsMatch(n, @"^(?:[A-Za-z]|\.(?![0-9]))[A-Za-z0-9._]*$") && !Reserved.Contains(n) ? n : "`" + n.Replace("\\", "\\\\").Replace("`", "\\`") + "`";
    private static string Q(string s) => Recipe.RQuote(s);
    private static string Vec(IEnumerable<string> values) { var list = values.ToList(); return list.Count == 1 ? Q(list[0]) : "c(" + string.Join(", ", list.Select(Q)) + ")"; }
    private static string VecNA(IEnumerable<string?> values) { var list = values.ToList(); return list.Count == 1 ? (list[0] == null ? "NA" : Q(list[0]!)) : "c(" + string.Join(", ", list.Select(v => v == null ? "NA" : Q(v))) + ")"; }
    private static string Col(string obj, string name) => obj + "$" + Name(name);
    private static bool Syntactic(string n) => Name(n) == n;
    private static string Lit(ValueKind kind, string value) => kind == ValueKind.Text ? Q(value) : value.Length == 0 ? "NA" : kind switch
    {
        ValueKind.Number => value, ValueKind.Date => "as.Date(" + Q(value) + ")", _ => value == "true" ? "TRUE" : "FALSE"
    };
    private static string Lits(ValueKind kind, IEnumerable<string> values) { var list = values.ToList(); return list.Count == 1 ? Lit(kind, list[0]) : "c(" + string.Join(", ", list.Select(v => Lit(kind, v))) + ")"; }
    private static string NaOf(ValueKind kind) => kind switch { ValueKind.Number => "NA_real_", ValueKind.Date => "as.Date(NA)", ValueKind.Boolean => "NA", _ => "NA_character_" };

    public static string Generate(CodePlan plan, bool portable, string datasetName)
    {
        var m = CodeModel.Build(plan);
        var body = new StringBuilder();
        bool needYesNo = false, needAsText = false, needKey = false, needStackColumns = false, needSpread = false;
        void L(string line = "") => body.Append(CodeModel.Wrap(line, false)).Append('\n');
        void Note(string text) => body.Append("# ").Append(text).Append('\n');

        var objects = m.ObjectNames(40, ["items", "prepared", "long", "ids", "keys", "key", "folder", "read_text", "yes_no", "as_text", "stack_columns", "spread", "as_row", "context", "num", "dates", "flags", "text_cols", "v", "m", "i", "rows", "hit"]);
        string Obj(FilePlan f) => objects[m.Files.IndexOf(f)];

        // ---- 2. Clean each file ----
        bool anyCleaning = m.Files.Any(f => f.Ops.Count > 0);
        if (anyCleaning) { L(); L("# ---- 2. Clean each file ----"); }
        foreach (var file in m.Files.Where(f => f.Ops.Count > 0))
        {
            string o = Obj(file); var s = file.Source;
            var names = s.Fields.Select(f => f.Original).ToArray();
            L(); Note(s.Name);
            Type? previous = null;
            foreach (var op in file.Ops)
            {
                bool first = op.GetType() != previous; previous = op.GetType();
                switch (op)
                {
                    case RenameOp rename:
                        Note("Rename columns");
                        if (rename.Items.Count <= 3) foreach (var (_, from, to) in rename.Items) L($"names({o})[names({o}) == {Q(from)}] <- {Q(to)}");
                        else L($"names({o})[match({Vec(rename.Items.Select(x => x.From))}, names({o}))] <- {Vec(rename.Items.Select(x => x.To))}");
                        foreach (var (c, _, to) in rename.Items) names[c] = to;
                        break;
                    case CellOp cell:
                        if (first) Note("Correct single values (row = data row, not counting the header)");
                        L($"{o}[{cell.Row + 1}, {Q(names[cell.Col])}] <- {Lit(cell.Kind, cell.After)}  # was {CodeModel.Show(cell.Before)}");
                        break;
                    case ReplaceOp replace:
                        if (first) Note("Recode values");
                        string target = Col(o, names[replace.Col]);
                        string condition = replace.Kind != ValueKind.Text && replace.Before.Length == 0 ? $"is.na({target})" : $"{target} %in% {Lit(replace.Kind, replace.Before)}";
                        L($"{target}[{condition}] <- {Lit(replace.Kind, replace.After)}");
                        break;
                    case ConvertOp convert:
                    {
                        string what = convert.Target switch
                        {
                            ValueKind.Number => "Store as numbers", ValueKind.Date => $"Store as dates (written as {convert.DateFormat})",
                            ValueKind.Boolean => "Store as TRUE/FALSE (true/yes/1 and false/no/0)", _ => "Store as text"
                        };
                        Note(what + (convert.Tokens.Length == 0 ? "" : $"; {CodeModel.ShowList(convert.Tokens)} count{(convert.Tokens.Length == 1 ? "s" : "")} as missing"));
                        if (convert.Target == ValueKind.Boolean) needYesNo = true;
                        if (convert.From != ValueKind.Text || convert.Target == ValueKind.Text) needAsText = true;
                        string Expression(string x)
                        {
                            string source = convert.From == ValueKind.Text ? x : $"as_text({x})";
                            if (convert.Tokens.Length > 0) source = $"replace({source}, {source} %in% {Vec(convert.Tokens)}, NA)";
                            return convert.Target switch
                            {
                                ValueKind.Number => $"as.numeric({source})", ValueKind.Date => $"as.Date({source}, {Q(Recipe.RDateFormat(convert.DateFormat))})",
                                ValueKind.Boolean => $"yes_no({source})", _ => convert.From == ValueKind.Text ? source : $"as_text({x})"
                            };
                        }
                        if (convert.Cols.Count == 1) { string x = Col(o, names[convert.Cols[0]]); L($"{x} <- {Expression(x)}"); break; }
                        string v = convert.Target switch { ValueKind.Number => "num", ValueKind.Date => "dates", ValueKind.Boolean => "flags", _ => "text_cols" };
                        L($"{v} <- {Vec(convert.Cols.Select(c => names[c]))}");
                        string simple = convert.Tokens.Length == 0 && convert.From == ValueKind.Text && convert.Target is ValueKind.Number or ValueKind.Boolean ? (convert.Target == ValueKind.Number ? "as.numeric" : "yes_no") : $"function(x) {Expression("x")}";
                        L($"{o}[{v}] <- lapply({o}[{v}], {simple})");
                        break;
                    }
                    case KeyNormalizeOp key:
                    {
                        string x = Col(o, names[key.Col]);
                        Note(key.Target switch
                        {
                            ValueKind.Number => $"Matching column {names[key.Col]} as numbers (00001 -> 1); kept as text until the files are combined",
                            ValueKind.Date => $"Matching column {names[key.Col]} as dates (written as {key.DateFormat}); kept as yyyy-mm-dd text until the files are combined",
                            _ => $"Matching column {names[key.Col]} as true/false; kept as text until the files are combined"
                        });
                        if (key.Target == ValueKind.Boolean) needYesNo = true; else needAsText = true;
                        L($"{x} <- " + key.Target switch
                        {
                            ValueKind.Number => $"as_text(as.numeric({x}))",
                            ValueKind.Date => $"format(as.Date({x}, {Q(Recipe.RDateFormat(key.DateFormat))}), \"%Y-%m-%d\")",
                            _ => $"tolower(yes_no({x}))"
                        });
                        break;
                    }
                    case ScaleOp scale:
                    {
                        var sc = scale.Scale; var itemNames = scale.Items.Select(c => names[c]).ToList();
                        string range = (sc.ReverseMin + sc.ReverseMax).ToString(CultureInfo.InvariantCulture);
                        Note($"Scale score {names[scale.Col]}: {sc.Method.ToString().ToLowerInvariant()} of {string.Join(", ", itemNames)}" + (sc.Reversed.Count > 0 ? $"; {string.Join(", ", sc.Reversed.Select(r => names[s.Fields.FindIndex(f => f.Original == r)]))} reverse-coded as {range} - x" : "") + $"; needs at least {sc.MinItems} answered item{(sc.MinItems == 1 ? "" : "s")}");
                        L($"items <- {o}[{Vec(itemNames)}]");
                        foreach (var reversed in sc.Reversed) { string name = names[s.Fields.FindIndex(f => f.Original == reversed)]; L($"items[[{Q(name)}]] <- {range} - items[[{Q(name)}]]"); }
                        L($"{Col(o, names[scale.Col])} <- ifelse(rowSums(!is.na(items)) >= {sc.MinItems}, {(sc.Method == ScaleMethod.Sum ? "rowSums" : "rowMeans")}(items, na.rm = TRUE), NA_real_)");
                        break;
                    }
                    case MissingOp missing:
                        if (first) Note("Missing-value codes become NA");
                        if (missing.Cols.Count == 1) { string x = Col(o, names[missing.Cols[0]]); L($"{x}[{x} %in% {Lits(missing.Kind, missing.Tokens)}] <- NA"); }
                        else L($"for (v in {Vec(missing.Cols.Select(c => names[c]))}) {o}[[v]][{o}[[v]] %in% {Lits(missing.Kind, missing.Tokens)}] <- NA");
                        break;
                    case KeepOp keep:
                    {
                        Note("Filter responses: keep only the checked answers");
                        var parts = keep.Rules.Select(r =>
                        {
                            string x = Col(o, names[r.Col]); string inSet = r.Values.Length == 0 ? "FALSE" : $"{x} %in% {Lits(r.Kind, r.Values)}";
                            return r.KeepMissing ? $"({inSet} | is.na({x}))" : inSet;
                        }).ToList();
                        var removed = keep.Rules.SelectMany(r => r.Removed.Select(CodeModel.Show).Concat(r.RemovesMissing ? ["missing"] : [])).ToList();
                        L($"{o} <- {o}[{string.Join(" & ", parts)}, ]" + (removed.Count == 0 ? "" : "  # leaves out " + string.Join(", ", removed.Take(6)) + (removed.Count > 6 ? ", …" : "")));
                        break;
                    }
                    case SelectOp select:
                        Note("Keep the selected variables");
                        if (select.Drop.Count == 1) L($"{Col(o, names[select.Drop[0]])} <- NULL");
                        else if (select.Drop.Count <= select.Keep.Count) L($"{o}[{Vec(select.Drop.Select(c => names[c]))}] <- NULL");
                        else L($"{o} <- {o}[{Vec(select.Keep.Select(c => names[c]))}]");
                        break;
                }
            }
        }

        // ---- 3. Combine ----
        var columns = new List<string>(); // column order of `prepared` as built
        var found = new Dictionary<string, string>(StringComparer.Ordinal); // variable key -> R expression of "record has this variable"
        string id = m.IdName, g = m.GroupName, e = m.EntityName ?? "";
        string Df(string args, bool anyOddNames) => $"data.frame({args}{(anyOddNames ? ", check.names = FALSE" : "")})";
        string idCol = Col("prepared", id), gCol = Col("prepared", g);
        bool oddKeys = !Syntactic(id) || !Syntactic(g) || (m.HasEntity && !Syntactic(e));
        string TimeColumnOf(FilePlan f) => f.Source.Fields.Any(x => x.Name == g) && !f.HasTimeColumn ? g + "_label" : g;
        string LongObj(FilePlan f) => Obj(f) + "_long";
        string KeyName(FilePlan f, int column) => f.Source.Fields[column].Name;
        bool measurement = m.Plan.Layout == Layout.Measurement;
        var stackedKeys = m.Plan.Preferences.RowVariables ?? new HashSet<string>(StringComparer.Ordinal);

        void Reshape(FilePlan f, IEnumerable<string> carry)
        {
            needStackColumns = true;
            string o = Obj(f);
            Note($"{f.Source.Name} has one column per {g}; give each {g} its own row");
            string measures = Vec(f.Measures); bool patterned = false;
            var entries = new List<string>();
            foreach (string t in f.Occasions)
            {
                var cols = f.Measures.Select(ms => f.Repeated.TryGetValue((ms, t), out int c) ? f.Source.Fields[c].Name : null).ToList();
                string? expression = null;
                if (cols.All(c => c != null) && f.Measures.Count > 1)
                {
                    var suffixes = cols.Select((c, i) => c!.StartsWith(f.Measures[i], StringComparison.Ordinal) ? c[f.Measures[i].Length..] : null).Distinct().ToList();
                    var prefixes = cols.Select((c, i) => c!.EndsWith(f.Measures[i], StringComparison.Ordinal) ? c[..^f.Measures[i].Length] : null).Distinct().ToList();
                    if (suffixes.Count == 1 && suffixes[0] is { Length: > 0 } suffix) { expression = $"paste0(m, {Q(suffix)})"; patterned = true; }
                    else if (prefixes.Count == 1 && prefixes[0] is { Length: > 0 } prefix) { expression = $"paste0({Q(prefix)}, m)"; patterned = true; }
                }
                entries.Add($"{Q(t)} = {expression ?? VecNA(cols)}");
            }
            if (patterned) L($"m <- {measures}");
            L($"{LongObj(f)} <- stack_columns({o}, {Vec(carry)}, {Q(TimeColumnOf(f))}, list({string.Join(", ", entries)}), {(patterned ? "m" : measures)})");
        }

        if (m.Plan.Layout == Layout.Source)
        {
            var file = m.Files[0];
            L(); L($"# ---- 3. Use the rows of {file.Source.Name} ----");
            L($"prepared <- {Obj(file)}");
            columns.AddRange(Enumerable.Range(0, file.Source.Fields.Count).Where(c => file.Source.Fields[c].Keep || file.Source.IsKeyColumn(c)).Select(c => file.Source.Fields[c].Name));
        }
        else if (m.Plan.Stack)
        {
            L(); L("# ---- 3. Stack the files: rows stay separate, and source_file names each row's file ----");
            var unified = m.Plan.Variables.ToList(); // every variable of the long layout, including ones stacked into rows later
            var partNames = new List<string>();
            foreach (var f in m.Files)
            {
                string o = Obj(f), part = o + "_part", rowsObj = o;
                var keyCols = new List<string> { KeyName(f, f.Source.IdColumn) };
                if (f.HasEntity) keyCols.Add(KeyName(f, f.Source.EntityColumn));
                if (f.HasTimeColumn) keyCols.Add(KeyName(f, f.Source.TimeColumn));
                if (f.Wide) { Reshape(f, keyCols.Concat(f.Basic.Select(c => f.Source.Fields[c].Name))); rowsObj = LongObj(f); }
                if (f.Timed || f.HasEntity)
                {
                    string idx = Col(rowsObj, keyCols[0]);
                    string order = $"match({idx}, {idx})";
                    if (f.HasEntity) { needKey = true; string k = $"key({idx}, {Col(rowsObj, keyCols[1])})"; order += $", match({k}, {k})"; }
                    Note("Keep each ID's rows together, in the order the IDs first appear");
                    L($"{rowsObj} <- {rowsObj}[order({order}), ]");
                }
                var args = new List<string> { $"{Name(id)} = {Col(rowsObj, keyCols[0])}" };
                if (m.HasEntity) args.Add($"{Name(e)} = " + (f.HasEntity ? Col(rowsObj, keyCols[1]) : "NA_character_"));
                args.Add($"{Name(g)} = " + (f.HasTimeColumn ? Col(rowsObj, keyCols[^1]) : f.Wide ? Col(rowsObj, TimeColumnOf(f)) : "NA_character_"));
                foreach (var u in unified)
                {
                    if (u.Key.StartsWith('\u001f')) { args.Add($"{Name(u.Name)} = {Q(f.Source.Name)}"); continue; }
                    string variable = u.Key["stack:".Length..];
                    int basic = f.Basic.FirstOrDefault(c => f.Source.Fields[c].Name == variable, -1);
                    string value = basic >= 0 ? Col(rowsObj, f.Source.Fields[basic].Name) : f.Measures.Contains(variable) ? Col(rowsObj, variable) : NaOf(u.Kind);
                    args.Add($"{Name(u.Name)} = {value}");
                }
                L($"{part} <- {Df(string.Join(", ", args), oddKeys || unified.Any(u => !Syntactic(u.Name)))}");
                partNames.Add(part);
            }
            L($"prepared <- {(partNames.Count == 1 ? partNames[0] : "rbind(" + string.Join(", ", partNames) + ")")}");
            columns.Add(id); if (m.HasEntity) columns.Add(e); columns.Add(g); columns.AddRange(unified.Select(u => u.Name));
            string sourceFile = unified.First(u => u.Key.StartsWith('\u001f')).Name;
            foreach (var u in unified.Where(u => !u.Key.StartsWith('\u001f')))
            {
                string variable = u.Key["stack:".Length..];
                var having = m.Files.Where(f => f.Basic.Any(c => f.Source.Fields[c].Name == variable) || f.Measures.Contains(variable)).Select(f => f.Source.Name).ToList();
                found[u.Key] = having.Count == m.Files.Count ? "rep(TRUE, nrow(prepared))" : $"{Col("prepared", sourceFile)} %in% {Vec(having)}";
            }
            found[unified.First(u => u.Key.StartsWith('\u001f')).Key] = "rep(TRUE, nrow(prepared))";
        }
        else
        {
            L(); L("# ---- 3. Combine the files ----");
            foreach (var f in m.Files.Where(f => f.Wide))
            {
                var carry = new List<string> { KeyName(f, f.Source.IdColumn) };
                if (f.HasEntity) carry.Add(KeyName(f, f.Source.EntityColumn));
                Reshape(f, carry);
            }
            string IdOf(FilePlan f) => Col(Obj(f), KeyName(f, f.Source.IdColumn));
            Note("IDs in the order they first appear" + (m.Plan.Match == MatchMode.CommonPeople ? ", keeping IDs found in every file" : m.Plan.Match == MatchMode.FirstFilePeople ? ", keeping IDs found in the first file" : ""));
            L($"ids <- unique({(m.Files.Count == 1 ? IdOf(m.Files[0]) : "c(" + string.Join(", ", m.Files.Select(IdOf)) + ")")})");
            if (m.Plan.Match == MatchMode.CommonPeople && m.Files.Count > 1) L($"ids <- ids[{string.Join(" & ", m.Files.Select(f => "ids %in% " + IdOf(f)))}]");
            if (m.Plan.Match == MatchMode.FirstFilePeople && m.Files.Count > 1) L($"ids <- ids[ids %in% {IdOf(m.Files[0])}]");
            var timed = m.Files.Where(f => f.Timed).ToList();
            if (!m.HasEntity && timed.Count == 0)
            {
                Note("One row per ID");
                L($"prepared <- {Df($"{Name(id)} = ids, {Name(g)} = rep(NA_character_, length(ids))", oddKeys)}");
            }
            else if (!m.HasEntity)
            {
                Note($"One row per ID and {g}, in the order they first appear");
                var frames = timed.Select(f => { string src = f.Wide ? LongObj(f) : Obj(f); string idName = KeyName(f, f.Source.IdColumn); string t = f.Wide ? TimeColumnOf(f) : KeyName(f, f.Source.TimeColumn); return Df($"{Name(id)} = {Col(src, idName)}, {Name(g)} = {Col(src, t)}", oddKeys); }).ToList();
                L($"prepared <- unique({(frames.Count == 1 ? frames[0] : "rbind(" + string.Join(", ", frames) + ")")})");
                if (timed.Count < m.Files.Count)
                {
                    Note($"IDs that only appear in files without a {g} keep one row");
                    string noTime = "no_" + (g.ToLowerInvariant() == g && Syntactic(g) ? g : "time");
                    L($"{noTime} <- setdiff(ids, {idCol})");
                    L($"if (length({noTime})) prepared <- rbind(prepared, {Df($"{Name(id)} = {noTime}, {Name(g)} = NA_character_", oddKeys)})");
                }
                L($"prepared <- prepared[order(match({idCol}, ids)), ]" + (m.Plan.Match == MatchMode.AllPeople ? "" : $"\nprepared <- prepared[{idCol} %in% ids, ]"));
            }
            else
            {
                Note($"One row per ID, {e} and {g}. Files without a {e} column apply to every {e} of that ID;");
                Note($"files without a {g} apply to every {g}.");
                var frames = m.Files.Select(f =>
                {
                    string src = f.Wide ? LongObj(f) : Obj(f);
                    string person = f.HasEntity ? Col(src, KeyName(f, f.Source.EntityColumn)) : "NA";
                    string time = f.HasTimeColumn ? Col(src, KeyName(f, f.Source.TimeColumn)) : f.Wide ? Col(src, TimeColumnOf(f)) : "NA";
                    return Df($"{Name(id)} = {Col(src, KeyName(f, f.Source.IdColumn))}, {Name(e)} = {person}, {Name(g)} = {time}", oddKeys);
                }).ToList();
                L($"keys <- {(frames.Count == 1 ? frames[0] : "rbind(" + string.Join(", ", frames) + ")")}");
                string kId = Col("k", id), kE = Col("k", e), kG = Col("k", g);
                L($"prepared <- do.call(rbind, lapply(split(keys, factor({Col("keys", id)}, levels = ids)), function(k) {{");
                L($"  people <- unique({kE}[!is.na({kE})])");
                L("  if (!length(people)) people <- NA_character_");
                L("  do.call(rbind, lapply(people, function(p) {");
                L($"    times <- unique({kG}[!is.na({kG}) & (is.na({kE}) | {kE} %in% p)])");
                L($"    {Df($"{Name(id)} = {kId}[1], {Name(e)} = p, {Name(g)} = if (length(times)) times else NA_character_", oddKeys)}");
                L("  }))");
                L("}))");
            }
            columns.Add(id); if (m.HasEntity) columns.Add(e); columns.Add(g);

            // Add each file's variables, matched on the keys that file has.
            var attaches = new List<(FilePlan File, bool Long, List<string> From, List<string> To, List<string> Keys)>();
            foreach (var f in m.Files)
            {
                if (f.Basic.Count > 0)
                {
                    var keys = new List<string> { KeyName(f, f.Source.IdColumn) };
                    if (f.HasEntity) keys.Add(KeyName(f, f.Source.EntityColumn));
                    if (f.HasTimeColumn) keys.Add(KeyName(f, f.Source.TimeColumn));
                    attaches.Add((f, false, f.Basic.Select(c => f.Source.Fields[c].Name).ToList(), f.Basic.Select(c => m.Label(f, c)).ToList(), keys));
                }
                if (f.Wide)
                {
                    var keys = new List<string> { KeyName(f, f.Source.IdColumn) };
                    if (f.HasEntity) keys.Add(KeyName(f, f.Source.EntityColumn));
                    keys.Add(TimeColumnOf(f));
                    attaches.Add((f, true, f.Measures.ToList(), f.Measures.Select(x => m.MeasureLabel(f, x)).ToList(), keys));
                }
            }
            if (attaches.Count > 0)
            {
                needKey = attaches.Any(a => a.Keys.Count > 1);
                L(); Note("Add each file's variables. Values from a file without a " + (m.HasEntity ? e + " or " : "") + g + " column repeat on every matching row.");
                foreach (var a in attaches)
                {
                    string src = a.Long ? LongObj(a.File) : Obj(a.File);
                    var mine = new List<string> { idCol }; var theirs = new List<string> { Col(src, a.Keys[0]) };
                    if (a.File.HasEntity) { mine.Add(Col("prepared", e)); theirs.Add(Col(src, a.Keys[1])); }
                    if (a.Long || a.File.HasTimeColumn) { mine.Add(gCol); theirs.Add(Col(src, a.Keys[^1])); }
                    L(mine.Count == 1 ? $"i <- match({mine[0]}, {theirs[0]})" : $"i <- match(key({string.Join(", ", mine)}), key({string.Join(", ", theirs)}))");
                    if (a.To.SequenceEqual(a.From) && a.To.Count > 3) { L($"v <- {Vec(a.To)}"); L($"prepared[v] <- {src}[i, v]"); }
                    else L($"prepared[{Vec(a.To)}] <- {src}[i, {Vec(a.From)}]");
                    columns.AddRange(a.To);
                    if (measurement)
                    {
                        var keysHere = a.Long ? a.File.Measures.Select(x => m.MeasureKey(a.File, x)) : a.File.Basic.Select(c => m.VariableKey(a.File, c));
                        if (keysHere.Any(stackedKeys.Contains)) { string flag = "found_" + src; L($"{flag} <- !is.na(i)"); foreach (var k in keysHere) found[k] = flag; }
                    }
                }
            }
        }

        // ---- Layout ----
        if (m.Plan.Layout == Layout.Person)
        {
            needSpread = true;
            L(); L("# ---- One row per ID: each " + (m.HasEntity ? e + " and " : "") + g + " gets its own column ----");
            L("long <- prepared");
            L($"prepared <- {Df($"{Name(id)} = ids", !Syntactic(id))}");
            var vars = new List<(string Key, string Label, List<(string? Entity, string? Time)> Pairs)>();
            foreach (var field in m.Raw.Fields.Skip(1))
            {
                var parts = field.Key.Split("\u001ftime:", 2); var entityParts = parts[0].Split("\u001fentity:", 2);
                string? entity = entityParts.Length == 2 && entityParts[1].Length > 0 ? entityParts[1] : null; string? time = parts.Length == 2 ? parts[1] : null;
                if (vars.Count > 0 && vars[^1].Key == entityParts[0]) vars[^1].Pairs.Add((entity, time));
                else vars.Add((entityParts[0], m.Labels[entityParts[0]], [(entity, time)]));
            }
            int index = 0;
            while (index < vars.Count)
            {
                var pairs = vars[index].Pairs; int end = index + 1;
                bool Same(List<(string?, string?)> other) => other.SequenceEqual(pairs);
                while (end < vars.Count && Same(vars[end].Pairs)) end++;
                var labels = vars.Skip(index).Take(end - index).Select(v => v.Label).ToList();
                if (pairs.Count == 1 && pairs[0].Entity == null && pairs[0].Time == null)
                    L($"prepared[{Vec(labels)}] <- long[match({idCol}, {Col("long", id)}), {Vec(labels)}]");
                else
                {
                    var people = pairs.Select(p => p.Entity).ToList(); var times = pairs.Select(p => p.Time).ToList();
                    string Arg(string nameArg, List<string?> values) => values.All(x => x == null) ? "" : ", " + nameArg + " = " + (values.Distinct().Count() == 1 ? VecNA([values[0]]) : VecNA(values));
                    string args = (m.HasEntity ? Arg("person", people) : "") + Arg("time", times);
                    if (labels.Count == 1) L($"prepared <- cbind(prepared, spread({Q(labels[0])}{args}))");
                    else L($"for (v in {Vec(labels)}) prepared <- cbind(prepared, spread(v{args}))");
                }
                index = end;
            }
            columns.Clear(); columns.AddRange(m.Raw.Fields.Select(f => f.Name));
        }
        else if (measurement)
        {
            var stackedFields = m.Raw.Fields.Where(f => f.Key == "@variable" || f.Key == "@value" || f.Key == "@source").ToList();
            var context = m.Raw.Fields.TakeWhile(f => f.Key != "@variable").Select(f => f.Name).ToList();
            var valueField = m.RawField("@value")!;
            var stacked = m.Plan.Variables.Select((v, i) => (v, i)).Where(x => x.v.Stacked).Select(x => x.v).ToList();
            bool mixed = valueField.Kind == ValueKind.Text && stacked.Any(v => v.Kind != ValueKind.Text);
            if (mixed) needAsText = true;
            L(); L("# ---- Put the chosen variables into variable/value rows ----");
            L($"context <- {Vec(context)}");
            string valueExpr = mixed ? "as_text(prepared[[var]][found])" : "prepared[[var]][found]";
            L($"as_row <- function(var, found, source) data.frame(.row = which(found), prepared[found, context, drop = FALSE], {Name(stackedFields[0].Name)} = var, {Name(stackedFields[1].Name)} = {valueExpr}, {Name(stackedFields[2].Name)} = source, check.names = FALSE)");
            var rowsCode = stacked.Select(v => $"as_row({Q(v.Name)}, {found.GetValueOrDefault(v.Key, "rep(TRUE, nrow(prepared))")}, {Q(v.Source)})").ToList();
            L($"prepared <- rbind({string.Join(", ", rowsCode)})");
            L("prepared <- prepared[order(prepared$.row), names(prepared) != \".row\"]");
            columns.Clear(); columns.AddRange(m.Raw.Fields.Select(f => f.Name));
        }

        // ---- 4. Final ----
        var final = new StringBuilder();
        void F(string line) => final.Append(CodeModel.Wrap(line, false)).Append('\n');
        var keyTypes = new List<(string Name, ValueKind Kind)>();
        if (m.Plan.Layout == Layout.Source)
        {
            var s = m.Files[0].Source;
            foreach (int c in new[] { s.IdColumn, s.EntityColumn, s.TimeColumn }.Where(c => c >= 0).Order()) if (s.Fields[c].Kind != ValueKind.Text) keyTypes.Add((s.Fields[c].Name, s.Fields[c].Kind));
        }
        else foreach (var field in m.Raw.Fields.Where(f => f.Key is "@person_id" or "@entity" or "@time" && f.Kind != ValueKind.Text)) keyTypes.Add((field.Name, field.Kind));
        if (keyTypes.Count > 0)
        {
            F("# Matching columns stayed text while combining; give them their final type");
            foreach (var (name, kind) in keyTypes)
            {
                string x = Col("prepared", name);
                if (kind == ValueKind.Boolean) needYesNo = true;
                F($"{x} <- " + kind switch { ValueKind.Number => $"as.numeric({x})", ValueKind.Date => $"as.Date({x})", _ => $"yes_no({x})" });
            }
        }
        var rawByKey = m.Raw.Fields.Select((f, i) => (f, i)).ToDictionary(x => Presentation.Identity(x.f, x.i), x => x.f.Name, StringComparer.Ordinal);
        var wanted = m.Plan.Columns.Select(c => rawByKey[c.Field.Key]).ToList();
        if (!wanted.SequenceEqual(columns))
        {
            F("# Column order");
            int k = 0;
            for (; k <= wanted.Count; k++) if (wanted.Skip(k).SequenceEqual(columns.Where(c => !wanted.Take(k).Contains(c)))) break;
            F(k < wanted.Count && k > 0 ? $"prepared <- prepared[c({string.Join(", ", wanted.Take(k).Select(Q))}, setdiff(names(prepared), {Vec(wanted.Take(k))}))]" : $"prepared <- prepared[{Vec(wanted)}]");
        }
        var renames = m.Plan.Columns.Select((c, i) => (From: wanted[i], To: c.Field.Name)).Where(x => x.From != x.To).ToList();
        if (renames.Count > 0)
        {
            F("# Column names chosen in WideLong");
            if (renames.Count <= 3) foreach (var (from, to) in renames) F($"names(prepared)[names(prepared) == {Q(from)}] <- {Q(to)}");
            else F($"names(prepared)[match({Vec(renames.Select(r => r.From))}, names(prepared))] <- {Vec(renames.Select(r => r.To))}");
        }
        string FinalName(string rawName) => m.Plan.Columns[wanted.IndexOf(rawName)].Field.Name;
        var compiled = Filtering.Compile(m.Raw.Fields, m.Plan.StructuredFilter);
        if (compiled.Count > 0)
        {
            string Predicate(CompiledFilterRule c)
            {
                var rule = c.Rule; string x = Name(FinalName(c.Field.Name));
                string Lv(string v) => rule.Kind switch { ValueKind.Number => v, ValueKind.Date => $"as.Date({Q(v)})", ValueKind.Boolean => v == "true" ? "TRUE" : "FALSE", _ => Q(v) };
                return rule.Operator switch
                {
                    FilterOperator.IsMissing => $"is.na({x})", FilterOperator.IsNotMissing => $"!is.na({x})",
                    FilterOperator.Contains => $"grepl({Q(rule.Value)}, {x}, fixed = TRUE)",
                    FilterOperator.NotContains => $"!is.na({x}) & !grepl({Q(rule.Value)}, {x}, fixed = TRUE)",
                    FilterOperator.Between => $"{x} >= {Lv(rule.Value)} & {x} <= {Lv(rule.UpperValue)}",
                    _ => $"{x} {rule.Operator switch { FilterOperator.Equal => "==", FilterOperator.NotEqual => "!=", FilterOperator.Greater => ">", FilterOperator.GreaterEqual => ">=", FilterOperator.Less => "<", _ => "<=" }} {Lv(rule.Value)}"
                };
            }
            F($"# Filter rules ({(m.Plan.StructuredFilter.MatchAll ? "all must match" : "any may match")}); rows where a rule cannot be checked are left out");
            var predicates = compiled.Select(Predicate).ToList();
            string joined = predicates.Count == 1 ? predicates[0] : string.Join(m.Plan.StructuredFilter.MatchAll ? " & " : " | ", predicates.Select(x => "(" + x + ")"));
            F($"prepared <- subset(prepared, {joined})");
        }
        if (m.Plan.Filter.Length > 0)
        {
            needAsText = true;
            F($"# Search rows: keep rows where any value contains {CodeModel.Show(m.Plan.Filter)} (ignoring case)");
            F($"hit <- Reduce(`|`, lapply(prepared, function(x) !is.na(x) & grepl({Q(m.Plan.Filter.ToLowerInvariant())}, tolower(as_text(x)), fixed = TRUE)))");
            F("prepared <- prepared[hit, ]");
        }
        if (m.Plan.Sorts.Count > 0)
        {
            var keys = new List<string>(); var decreasing = new List<bool>(); var described = new List<string>();
            foreach (var (column, descending) in m.Plan.Sorts)
            {
                var field = m.Plan.Columns[column].Field; string x = Col("prepared", field.Name);
                string value = field.Kind == ValueKind.Text ? $"toupper({x})" : x;
                if (descending) { keys.Add($"is.na({x})"); decreasing.Add(false); }
                keys.Add(value); decreasing.Add(descending);
                described.Add(field.Name + (descending ? " (descending)" : ""));
            }
            F($"# Sort by {string.Join(", then ", described)}; blanks last, ties keep their order");
            string args = string.Join(", ", keys) + (decreasing.Any(d => d) ? ", decreasing = c(" + string.Join(", ", decreasing.Select(d => d ? "TRUE" : "FALSE")) + ")" : "") + ", method = \"radix\"";
            F($"prepared <- prepared[order({args}), ]");
        }
        F("rownames(prepared) <- NULL");
        var labelled = m.Plan.Columns.Select(c => c.Field).Where(Export.HasLabels).ToList();
        if (labelled.Count > 0)
        {
            F("# Value labels, kept as a \"labels\" attribute (read by haven/labelled). For a factor: factor(x, levels = labels, labels = names(labels))");
            foreach (var field in labelled) F($"attr({Col("prepared", field.Name)}, \"labels\") <- {Export.RLabelVector(field)}");
        }

        // ---- Assemble ----
        var b = new StringBuilder();
        void H(string line = "") => b.Append(CodeModel.Wrap(line, false)).Append('\n');
        H($"# {datasetName}: cleaning script written by WideLong {CodeModel.Version} on {CodeModel.Today}");
        H($"# Rebuilds the prepared dataset ({m.Final.Rows.Count:N0} rows x {m.Final.Fields.Count:N0} columns; {CodeModel.ShapeWord(m.Plan.Layout)})");
        H("# from the original files, step by step. Base R only. Nothing is saved until you run the last line.");
        H();
        H("# ---- 1. Read the original files (every column as text, so codes and leading zeros survive) ----");
        string? folder = m.CommonFolder(portable);
        if (folder != null) H($"folder <- {Q(folder.Replace('\\', '/'))}  # where the original files are");
        string path = folder != null ? "file.path(folder, file)" : "file";
        H("# read_text(): one original file, decoded from its encoding, with every column kept as text");
        // A one-column file can hold records whose only value is empty (""); those must not be skipped as blank lines.
        bool emptyRecords = m.Files.Any(f => f.Source.Fields.Count == 1 && f.Source.OriginalRows!.Any(r => r[0].Length == 0));
        H("read_text <- function(file, sep = \",\", encoding = \"UTF-8\"" + (emptyRecords ? ", keep_empty = FALSE" : "") + ") {");
        H($"  path <- {path}");
        H("  text <- sub(\"^\\ufeff\", \"\", iconv(list(readBin(path, \"raw\", file.size(path))), from = encoding, to = \"UTF-8\"))");
        if (emptyRecords) H("  if (keep_empty) text <- gsub(\"(^|\\n)(\\r?\\n)+\", \"\\\\1\", sub(\"\\r?\\n$\", \"\", text))  # drop blank lines, keep \"\" records");
        H("  read.csv(text = text, sep = sep, colClasses = \"character\", na.strings = character(0), check.names = FALSE, encoding = \"UTF-8\"" + (emptyRecords ? ", blank.lines.skip = !keep_empty" : "") + ")");
        H("}");
        int pad = objects.Max(x => x.Length);
        foreach (var f in m.Files)
        {
            string extra = (f.Source.Fields.Count == 1 && f.Source.OriginalRows!.Any(r => r[0].Length == 0) ? ", keep_empty = TRUE" : "") + (f.Source.Delimiter == ',' ? "" : ", sep = " + Q(f.Source.Delimiter.ToString())) + (f.Source.EncodingName switch { "windows-1252" => ", encoding = \"CP1252\"", "UTF-16LE" => ", encoding = \"UTF-16LE\"", _ => "" });
            H($"{Obj(f).PadRight(pad)} <- read_text({Q(m.FileReference(f, portable).Replace('\\', '/'))}{extra})");
        }
        foreach (var f in m.Files.Where(f => f.Source.SkipRows > 0))
            H($"{Obj(f)} <- {Obj(f)}[-seq_len({f.Source.SkipRows}), , drop = FALSE]; rownames({Obj(f)}) <- NULL  # skip {f.Source.SkipRows} description row{(f.Source.SkipRows == 1 ? "" : "s")} under the header" + (ImportPresets.LooksLikeRedcap(f.Source) ? "" : f.Source.SkipRows == 2 ? " (Qualtrics)" : ""));
        H("stopifnot(" + string.Join(", ", m.Files.Select(f => $"nrow({Obj(f)}) == {f.Source.OriginalRows!.Count}")) + ")  # expected data rows; stops if a file changed");
        if (needYesNo || needAsText || needKey || needStackColumns || needSpread)
        {
            H(); H("# Small helpers used below");
            if (needYesNo) H("yes_no <- function(x) unname(c(true = TRUE, yes = TRUE, \"1\" = TRUE, false = FALSE, no = FALSE, \"0\" = FALSE)[tolower(x)])");
            if (needAsText) H("as_text <- function(x) if (is.numeric(x)) ifelse(is.na(x), NA, trimws(formatC(x, digits = 15, format = \"fg\"))) else if (is.logical(x)) tolower(x) else as.character(x)");
            if (needKey) H("key <- function(...) paste(..., sep = \"\\r\")  # joins ID/person/time into one matching key");
            if (needStackColumns)
            {
                H("# stack_columns(): repeated columns (one per time) become rows; `columns` names each time's column for every measure");
                H("stack_columns <- function(data, keys, time, columns, measures) do.call(rbind, lapply(names(columns), function(t) {");
                H("  part <- data[keys]");
                H("  part[[time]] <- t");
                H("  part[measures] <- lapply(columns[[t]], function(col) if (is.na(col)) NA else data[[col]])");
                H("  part");
                H("}))");
            }
            if (needSpread)
            {
                string lId = Col("long", id), pId = Col("prepared", id), lG = Col("long", g);
                if (m.HasEntity)
                {
                    string lE = Col("long", e);
                    H("# spread(): one column per person/time pair from the long table, named variable__person__time");
                    H("spread <- function(var, person = NA, time = NA) {");
                    H("  cols <- mapply(function(p, t) {");
                    H($"    r <- long[(is.na(p) | {lE} %in% p) & (is.na(t) | {lG} %in% t), ]");
                    H($"    r[[var]][match({pId}, {Col("r", id)})]");
                    H("  }, person, time, SIMPLIFY = FALSE)");
                    H("  names(cols) <- paste0(var, ifelse(is.na(person), \"\", paste0(\"__\", person)), ifelse(is.na(time), \"\", paste0(\"__\", time)))");
                }
                else
                {
                    H("# spread(): one column per time from the long table, named variable__time");
                    H("spread <- function(var, time = NA) {");
                    H("  cols <- lapply(time, function(t) {");
                    H($"    r <- long[is.na(t) | {lG} %in% t, ]");
                    H($"    r[[var]][match({pId}, {Col("r", id)})]");
                    H("  })");
                    H("  names(cols) <- paste0(var, ifelse(is.na(time), \"\", paste0(\"__\", time)))");
                }
                H("  as.data.frame(cols, check.names = FALSE)");
                H("}");
                _ = lId;
            }
        }
        b.Append(body);
        b.Append('\n').Append("# ---- 4. Final columns, filters and sorting ----\n").Append(final);
        b.Append('\n').Append("# Save a copy (uncomment to write the file):\n");
        b.Append($"# write.csv(prepared, {Q(datasetName + ".csv")}, row.names = FALSE, na = \"\")\n");
        return b.ToString();
    }
}
