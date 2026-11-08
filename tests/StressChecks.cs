using WideLong;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Optional pressure test over a folder of the user's own CSV files (passed explicitly; never stored in this tree).
/// Every output cell is checked against its source record by an oracle that does not use the engine, and every
/// kept source value must appear in the combined layouts. Findings, not data values, are written to the report.
/// </summary>
static class StressChecks
{
    static readonly List<string> report = [];
    static int failures, passes;
    static string? rscript;
    static string outRoot = "";
    static readonly string[] MissingCodes = ["", "-99999", "-88888", "-77777", "-66666", "-55555", "-33333"];

    static void Log(string line) { report.Add(line); Console.WriteLine(line); }
    static void Pass(string name) { passes++; Log("PASS " + name); }
    static void Fail(string name, string detail) { failures++; Log("FAIL " + name + ": " + detail); }
    static void Info(string line) => Log("     " + line);
    static void Step(string name, Action action)
    {
        var watch = Stopwatch.StartNew();
        try { action(); }
        catch (Exception ex) { Fail(name, ex.GetType().Name + ": " + ex.Message.Split('\n')[0] + " @ " + (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim()); }
        if (watch.ElapsedMilliseconds > 1500) Info($"{name}: {watch.Elapsed.TotalSeconds:0.0} s");
    }

    public static int Run(string dataDir, string outputDir, string? rscriptPath)
    {
        outRoot = outputDir; Directory.CreateDirectory(outRoot);
        rscript = !string.IsNullOrWhiteSpace(rscriptPath) && File.Exists(rscriptPath) ? rscriptPath : null;
        Log($"Stress run over {Directory.GetFiles(dataDir, "*.csv").Length} files · R: {(rscript == null ? "not available" : "available")}");

        Session raw = new(), mapped = new(), typed = new();
        Step("Import with study missing codes and key recommendations", () => { raw = Load(dataDir); CheckKeys(raw); });
        if (raw.Sources.Count == 0) return Finish();
        Step("Matching diagnostics", () => KeyDiagnostics(raw));
        Step("Raw combined layouts keep every value with its own record", () => Layouts("raw", raw, measurement: false));

        Step("Map numbered repeated columns", () => { mapped = raw.Copy(); MapNumbered(mapped); });
        Step("Mapped combined layouts", () => Layouts("mapped", mapped, measurement: true));

        Step("Convert numeric variables, IDs and times", () => { typed = mapped.Copy(); ConvertTypes(typed); });
        Step("Typed combined layouts", () => Layouts("typed", typed, measurement: true));
        Step("Typed sorting, renaming and column order", () => Sorting(typed));
        Step("Typed AND / OR filter rules", () => Filters(typed));
        Step("Matching rules with incomplete files", () => MatchRules(typed));
        Step("Stacking the long files", () => Stacking(typed));
        Step("Random file combinations", () => Combinations(raw, typed));
        Step("Export bundles and SAS names", () => Exports(typed));
        Step("Cleaning code plans (R and SAS)", () => CodePlans(typed));
        if (rscript != null) Step("Native R replays", () => NativeR(raw, typed));
        return Finish();
    }

    static int Finish()
    {
        Log($"\n{passes} passed · {failures} failed");
        File.WriteAllLines(Path.Combine(outRoot, "stress-report.txt"), report, new UTF8Encoding(false));
        return failures == 0 ? 0 : 1;
    }

    static Session Load(string dir)
    {
        var session = new Session();
        foreach (string path in Directory.GetFiles(dir, "*.csv").OrderBy(p => p, StringComparer.Ordinal))
        {
            string text = File.ReadAllText(path, new UTF8Encoding(false, true));
            var source = Csv.Import(text, Path.GetFileName(path)); source.MissingTokens = [.. MissingCodes];
            session.Sources.Add(source);
        }
        KeyRecommendations.Recommend(session);
        return session;
    }
    static string Col(Source s, int c) => c < 0 ? "none" : s.Fields[c].Name;

    static void CheckKeys(Session session)
    {
        int bad = 0;
        foreach (var s in session.Sources)
        {
            int id = s.Fields.FindIndex(f => f.Name == "id"), person = s.Fields.FindIndex(f => f.Name == "person"), time = s.Fields.FindIndex(f => f.Name == "time");
            string got = $"{Col(s, s.IdColumn)}/{Col(s, s.EntityColumn)}/{Col(s, s.TimeColumn)}", want = $"{Col(s, id)}/{Col(s, person)}/{Col(s, time)}";
            if (got != want) { bad++; Info($"{s.Name}: recommended ID/additional key/time {got}; expected {want}"); }
        }
        if (bad == 0) Pass("Recommended keys match id / person / time in every file"); else Fail("Key recommendations", bad + " files need manual key choices");
        // Continue with the intended keys so later checks test merging rather than recommendations.
        foreach (var s in session.Sources)
        {
            s.IdColumn = s.Fields.FindIndex(f => f.Name == "id"); s.EntityColumn = s.Fields.FindIndex(f => f.Name == "person"); s.TimeColumn = s.Fields.FindIndex(f => f.Name == "time");
            s.IdChosen = s.EntityChosen = s.GroupChosen = true;
        }
    }

    static void KeyDiagnostics(Session session)
    {
        var reports = MatchingDiagnostics.Analyze(session);
        var problems = reports.Where(r => r.KeyStatus != "Complete, unique keys").ToList();
        foreach (var r in problems) Info($"{r.File}: {r.KeyStatus} · {r.MissingIdRows} missing ID, {r.MissingEntityRows} missing additional key, {r.MissingGroupingRows} missing grouping, {r.DuplicateRows} duplicate rows");
        if (problems.Count == 0) Pass("Diagnostics report complete, unique keys in every file"); else Fail("Diagnostics", problems.Count + " files report key problems");
    }

    static readonly Regex Numbered = new(@"^(.+)_(\d+)$");
    static void MapNumbered(Session session)
    {
        int groups = 0, columns = 0;
        foreach (var s in session.Sources.Where(s => s.TimeColumn < 0))
        {
            var maps = RepeatedColumnMapping.FromSource(s);
            var stems = maps.Select(m => Numbered.Match(m.Name)).Where(m => m.Success).GroupBy(m => m.Groups[1].Value).Where(g => g.Count() >= 2).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var map in maps)
            {
                var m = Numbered.Match(map.Name);
                if (m.Success && stems.Contains(m.Groups[1].Value)) { map.Variable = m.Groups[1].Value; map.Grouping = RepeatedColumnMapping.InferGrouping(map.Name); columns++; }
                else { map.Variable = ""; map.Grouping = ""; }
            }
            var errors = RepeatedColumnMapping.Validate(s, maps);
            if (errors.Count > 0) { Fail("Mapping " + s.Name, string.Join("; ", errors.Take(3))); continue; }
            RepeatedColumnMapping.Apply(s, maps); groups += stems.Count;
        }
        Pass($"Mapped {columns:N0} numbered columns into {groups:N0} repeated variables");
    }

    static void ConvertTypes(Session session)
    {
        int converted = 0;
        foreach (var s in session.Sources)
        {
            s.CaptureOriginal();
            // Like the type dialog: a repeated variable converts all of its columns together.
            var units = Enumerable.Range(0, s.Fields.Count).Where(c => c != s.EntityColumn).GroupBy(c => s.Fields[c].Measure.Length > 0 && !s.IsKeyColumn(c) ? "m:" + s.Fields[c].Measure : "c:" + c);
            foreach (var unit in units)
            {
                var present = unit.SelectMany(c => s.Rows.Select(r => r[c]).Where(v => !s.EffectiveMissingTokens(c).Contains(v))).ToList();
                if (present.Count == 0 || present.Any(v => !decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) continue;
                if (unit.Any(s.IsKeyColumn) && present.Any(v => Values.Convert(v, ValueKind.Number) != v)) { Info($"{s.Name}/{s.Fields[unit.First()].Name}: key not canonical, kept Text"); continue; }
                foreach (int c in unit)
                {
                    var tokens = s.EffectiveMissingTokens(c);
                    s.Steps.Add(new(StepKind.Convert, c, Target: ValueKind.Number, MissingTokens: tokens.ToArray()));
                    s.Fields[c].Kind = ValueKind.Number;
                    foreach (var row in s.Rows) row[c] = Values.ConvertWithMissing(row[c], ValueKind.Number, "yyyy-MM-dd", tokens);
                    converted++;
                }
            }
        }
        var kinds = Engine.KeyKinds(session);
        Info($"{converted:N0} columns converted to Number · combined ID {kinds.Id}, additional key {kinds.Entity}, time {kinds.Time}");
        if (kinds.Id == ValueKind.Number && kinds.Time == ValueKind.Number) Pass("Numeric ID and time carry through to the combined keys"); else Fail("Key types", $"ID {kinds.Id}, time {kinds.Time}");
    }

    static View Build(Session s, Layout layout, MatchMode match = MatchMode.AllPeople, bool stack = false, IEnumerable<string>? rows = null)
    {
        long before = GC.GetTotalMemory(true); var watch = Stopwatch.StartNew();
        var view = Engine.Build(s, layout, match, stack, rows);
        long cells = (long)view.Rows.Count * view.Fields.Count;
        Info($"{layout}{(stack ? " stacked" : "")}{(match == MatchMode.AllPeople ? "" : " " + match)}: {view.Rows.Count:N0} rows × {view.Fields.Count:N0} columns ({cells:N0} cells) in {watch.Elapsed.TotalSeconds:0.00} s, ~{(GC.GetTotalMemory(false) - before) / 1048576.0:N0} MB");
        return view;
    }

    static void Layouts(string label, Session s, bool measurement)
    {
        var data = s.Copy(includeOriginals: false);
        var longView = Build(data, Layout.PersonTime); Integrity(data, longView, Layout.PersonTime, label + " long", coverage: true);
        var wide = Build(data, Layout.Person); Integrity(data, wide, Layout.Person, label + " wide", coverage: true);
        if (measurement)
        {
            var rows = Engine.SuggestedRowVariables(data);
            if (rows.Count > 0) { var m = Build(data, Layout.Measurement, rows: rows); Integrity(data, m, Layout.Measurement, label + " custom rows", coverage: true); }
        }
        foreach (var source in data.Sources) Integrity(data, Engine.SourceView(source), Layout.Source, label + " source " + source.Name, coverage: true, quiet: true);
        Pass(label + ": every source view matches its file");
        GC.Collect();
    }

    static (string? Entity, string? Time) FieldScope(OutputField f)
    {
        string? entity = null, time = null;
        int t = f.Key.IndexOf("\u001ftime:", StringComparison.Ordinal); if (t >= 0) time = f.Key[(t + 6)..];
        int e = f.Key.IndexOf("\u001fentity:", StringComparison.Ordinal);
        if (e >= 0) { int end = t > e ? t : f.Key.Length; entity = f.Key[(e + 8)..end]; }
        return (entity, time);
    }

    /// <summary>Independent oracle: each cell must equal its origin, sit on its origin's ID/additional key/time, and carry its missing status.</summary>
    static void Integrity(Session session, View view, Layout layout, string label, bool coverage, bool quiet = false, bool stacked = false)
    {
        var problems = new List<string>(); void Problem(string text) { if (problems.Count < 6) problems.Add(text); else if (problems.Count == 6) problems.Add("…"); }
        var names = view.Fields.Select(f => f.Name).ToList();
        foreach (var dup in names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1)) Problem("duplicate output name " + dup.Key);
        if (view.Rows.Any(r => r.Length != view.Fields.Count)) Problem("row width differs from header");
        int idCol = view.Fields.FindIndex(f => f.Key == "@person_id"), entCol = view.Fields.FindIndex(f => f.Key == "@entity"), timeCol = view.Fields.FindIndex(f => f.Key == "@time");
        var sources = session.Sources.ToDictionary(s => s.Key);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var covered = new HashSet<(string, int, int)>();
        var scopes = view.Fields.Select(FieldScope).ToArray();
        for (int r = 0; r < view.Rows.Count; r++)
        {
            var row = view.Rows[r];
            string id = idCol >= 0 ? row[idCol].Value : "", entity = entCol >= 0 ? row[entCol].Value : "", time = timeCol >= 0 ? row[timeCol].Value : "";
            if (!stacked && (layout is Layout.PersonTime or Layout.Person) && !seenKeys.Add(id + "\u001f" + entity + "\u001f" + time)) Problem($"row {r + 1}: repeated output key");
            for (int c = 0; c < row.Length; c++)
            {
                var cell = row[c]; var o = cell.Origin;
                if (o == null) continue;
                if (!sources.TryGetValue(o.SourceKey, out var s)) { Problem("cell from an unknown file"); continue; }
                covered.Add((o.SourceKey, o.Row, o.Column));
                var src = s.Rows[o.Row];
                if (cell.Value != src[o.Column]) Problem($"{s.Name} record {o.Row + 2}, {s.Fields[o.Column].Name}: value differs from source");
                if (cell.Missing != s.IsMissing(src[o.Column], o.Column)) Problem($"{s.Name} record {o.Row + 2}, {s.Fields[o.Column].Name}: missing status differs from the file's rules");
                if (layout == Layout.Source) continue;
                if (src[s.IdColumn] != id) Problem($"{s.Name} record {o.Row + 2} placed under another ID");
                var (fieldEntity, fieldTime) = layout == Layout.Person ? scopes[c] : (entCol >= 0 ? entity : null, timeCol >= 0 ? time : null);
                if (s.EntityColumn >= 0 && fieldEntity is { Length: > 0 } && src[s.EntityColumn] != fieldEntity) Problem($"{s.Name} record {o.Row + 2}, {s.Fields[o.Column].Name}: placed under another additional key");
                if (s.TimeColumn >= 0 && fieldTime != null && src[s.TimeColumn] != fieldTime) Problem($"{s.Name} record {o.Row + 2}, {s.Fields[o.Column].Name}: placed at another time");
                var field = s.Fields[o.Column];
                if (field.Measure.Length > 0 && fieldTime != null && field.Occasion != fieldTime) Problem($"{s.Name}/{field.Name}: repeated column placed at time {fieldTime} instead of {field.Occasion}");
            }
        }
        if (coverage)
        {
            int lost = 0; string? example = null;
            foreach (var s in layout == Layout.Source ? session.Sources.Where(s => view.Fields.Any(f => f.Key.StartsWith(s.Key + ":", StringComparison.Ordinal))) : session.Sources)
                for (int r = 0; r < s.Rows.Count; r++)
                    for (int c = 0; c < s.Fields.Count; c++)
                        if (s.Fields[c].Keep && (layout == Layout.Source || !s.IsKeyColumn(c)) && !covered.Contains((s.Key, r, c))) { lost++; example ??= $"{s.Name} record {r + 2}, {s.Fields[c].Name}"; }
            if (lost > 0) Problem($"{lost:N0} source values do not appear (first: {example})");
        }
        if (problems.Count > 0) Fail(label, string.Join(" | ", problems));
        else if (!quiet) Pass(label + $": {view.Rows.Count:N0} rows, every value on its own record");
    }

    static double? Num(Cell c) => !c.Missing && double.TryParse(c.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;

    static void Sorting(Session s)
    {
        var raw = Build(s, Layout.PersonTime);
        var prefs = new ViewPreferences(); prefs.Reconcile(raw.Fields.Select(f => f.Key));
        var age = raw.Fields.FirstOrDefault(f => f.Kind == ValueKind.Number && !f.Key.StartsWith('@'));
        // UI order of operations: sort time, then ID, then a variable descending; sorted columns move to the front.
        prefs.ToggleSort(raw.Fields[raw.Fields.FindIndex(f => f.Key == "@time")].Key); prefs.MoveSortsToFront();
        prefs.ToggleSort(raw.Fields[0].Key); prefs.MoveSortsToFront();
        if (age != null) { prefs.ToggleSort(age.Key); prefs.Sorts[^1] = prefs.Sorts[^1] with { Descending = true }; prefs.MoveSortsToFront(); }
        prefs.MoveSort(raw.Fields[0].Key, prefs.Sorts[0].Key); prefs.MoveSortsToFront();
        prefs.Names[raw.Fields[0].Key] = "study_id";
        var watch = Stopwatch.StartNew(); var view = Presentation.Apply(raw, prefs.Copy()); Info($"Presentation with 3 sorts over {view.Rows.Count:N0} rows: {watch.Elapsed.TotalSeconds:0.00} s");
        var order = prefs.Sorts.Select(rule => (Index: view.Fields.FindIndex(f => f.Key == rule.Key), rule.Descending)).ToList();
        if (order.Select(o => o.Index).SequenceEqual(Enumerable.Range(0, order.Count))) Pass("Sorted columns lead in priority order"); else Fail("Sorted column order", string.Join(",", order.Select(o => o.Index)));
        if (view.Fields[0].Name != "study_id") Fail("Rename", "first column is " + view.Fields[0].Name);
        int bad = 0;
        for (int r = 1; r < view.Rows.Count; r++)
        {
            foreach (var (index, descending) in order)
            {
                Cell a = view.Rows[r - 1][index], b = view.Rows[r][index];
                int cmp;
                if (a.Missing || b.Missing) cmp = a.Missing == b.Missing ? 0 : a.Missing ? 1 : -1;
                else if (view.Fields[index].Kind == ValueKind.Number) { cmp = Num(a)!.Value.CompareTo(Num(b)!.Value); if (descending) cmp = -cmp; }
                else { cmp = string.Compare(a.Value.ToUpperInvariant(), b.Value.ToUpperInvariant(), StringComparison.Ordinal); if (descending) cmp = -cmp; }
                if (cmp < 0) break;
                if (cmp > 0) { bad++; break; }
            }
        }
        if (bad == 0) Pass($"Numeric multi-column sort holds across {view.Rows.Count:N0} rows (missing last)"); else Fail("Sort order", bad + " adjacent rows out of order");
    }

    static void Filters(Session s)
    {
        var raw = Build(s, Layout.PersonTime);
        var numeric = raw.Fields.Where(f => f.Kind == ValueKind.Number && !f.Key.StartsWith('@')).ToList();
        var entity = raw.Fields.FirstOrDefault(f => f.Key == "@entity");
        if (numeric.Count < 2 || entity == null) { Info("Not enough typed variables for filter checks"); return; }
        // Choose a variable with a spread of values so both rules select something.
        var a = numeric.OrderByDescending(f => raw.Rows.Select(r => r[raw.Fields.IndexOf(f)]).Where(c => !c.Missing).Select(c => c.Value).Distinct().Count()).First();
        int ai = raw.Fields.IndexOf(a), ei = raw.Fields.IndexOf(entity);
        var values = raw.Rows.Select(r => Num(r[ai])).Where(v => v != null).Select(v => v!.Value).OrderBy(v => v).ToList();
        string median = values[values.Count / 2].ToString(CultureInfo.InvariantCulture);
        string someEntity = raw.Rows.Select(r => r[ei].Value).First(v => v.Length > 0);
        var and = new RowFilterSpec { Rules = [new(a.Key, a.Name, a.Kind, FilterOperator.GreaterEqual, median), new(entity.Key, entity.Name, entity.Kind, FilterOperator.Equal, someEntity)] };
        var or = new RowFilterSpec { MatchAll = false, Rules = [new(a.Key, a.Name, a.Kind, FilterOperator.Less, median), new(a.Key, a.Name, a.Kind, FilterOperator.IsMissing)] };
        int expectAnd = raw.Rows.Count(r => Num(r[ai]) >= double.Parse(median, CultureInfo.InvariantCulture) && r[ei].Value == someEntity);
        int expectOr = raw.Rows.Count(r => r[ai].Missing || Num(r[ai]) < double.Parse(median, CultureInfo.InvariantCulture));
        int gotAnd = Filtering.Apply(raw, and).Rows.Count, gotOr = Filtering.Apply(raw, or).Rows.Count;
        if (gotAnd == expectAnd && gotOr == expectOr) Pass($"AND rule kept {gotAnd:N0}, OR rule kept {gotOr:N0} rows as computed independently"); else Fail("Filter rules", $"AND {gotAnd} vs {expectAnd}; OR {gotOr} vs {expectOr}");
        if (gotAnd + Filtering.Apply(raw, new RowFilterSpec { MatchAll = false, Rules = [new(a.Key, a.Name, a.Kind, FilterOperator.Less, median), new(a.Key, a.Name, a.Kind, FilterOperator.IsMissing), new(entity.Key, entity.Name, entity.Kind, FilterOperator.NotEqual, someEntity), new(entity.Key, entity.Name, entity.Kind, FilterOperator.IsMissing)] }).Rows.Count != raw.Rows.Count)
            Fail("Filter complement", "a rule set and its complement do not partition the rows");
        else Pass("A rule set and its complement partition every row");
    }

    static void MatchRules(Session s)
    {
        var data = s.Copy(includeOriginals: false);
        // Drop some IDs from two files to create incomplete matching.
        var first = data.Sources[0]; var other = data.Sources[^1];
        var ids = first.Rows.Select(r => r[first.IdColumn]).Distinct().ToList();
        var dropFirst = ids.Where((_, i) => i % 5 == 0).ToHashSet(); var dropOther = ids.Where((_, i) => i % 7 == 0).ToHashSet();
        first.Rows.RemoveAll(r => dropFirst.Contains(r[first.IdColumn])); other.Rows.RemoveAll(r => dropOther.Contains(r[other.IdColumn]));
        var all = data.Sources.SelectMany(x => x.Rows.Select(r => r[x.IdColumn])).ToHashSet();
        var common = all.Where(id => data.Sources.All(x => x.Rows.Any(r => r[x.IdColumn] == id))).ToHashSet();
        var firstIds = first.Rows.Select(r => r[first.IdColumn]).ToHashSet();
        foreach (var (mode, expected) in new[] { (MatchMode.AllPeople, all), (MatchMode.CommonPeople, common), (MatchMode.FirstFilePeople, firstIds) })
            foreach (var layout in new[] { Layout.PersonTime, Layout.Person })
            {
                var view = Engine.Build(data, layout, mode);
                var got = view.Rows.Select(r => r[0].Value).ToHashSet();
                if (got.SetEquals(expected)) Pass($"{mode} {layout}: {got.Count} IDs as expected"); else Fail($"{mode} {layout}", $"{got.Count} IDs, expected {expected.Count}");
                if (mode == MatchMode.AllPeople) Integrity(data, view, layout, $"incomplete files {layout}", coverage: true);
            }
    }

    static void Stacking(Session s)
    {
        var data = new Session { Sources = s.Copy(false).Sources.Where(x => x.TimeColumn >= 0).ToList() };
        if (data.Sources.Count < 2) return;
        try { var view = Build(data, Layout.PersonTime, stack: true); Integrity(data, view, Layout.PersonTime, "stacked long files", coverage: true, stacked: true); }
        catch (InvalidOperationException ex) { Info("Stacking refused: " + ex.Message); Pass("Stacking incompatible files is refused with an explanation"); }
    }

    static void Combinations(Session raw, Session typed)
    {
        var random = new Random(20261107); var messages = new Dictionary<string, int>(StringComparer.Ordinal); int built = 0;
        for (int i = 0; i < 40; i++)
        {
            var baseSession = i % 2 == 0 ? raw : typed;
            var picks = baseSession.Sources.OrderBy(_ => random.Next()).Take(random.Next(2, 7)).ToList();
            var data = new Session { Sources = picks.Select(p => p.Copy(false)).ToList() };
            // Vary keys the way a user might: sometimes ignore the additional key.
            if (i % 5 == 3) foreach (var x in data.Sources) x.EntityColumn = -1;
            foreach (var layout in new[] { Layout.PersonTime, Layout.Person })
            {
                string label = $"combo {i} {layout} [{string.Join(", ", data.Sources.Select(x => x.Name))}]";
                try { var view = Engine.Build(data, layout); Integrity(data, view, layout, label, coverage: true, quiet: true); built++; }
                catch (InvalidOperationException ex) { string key = Regex.Replace(ex.Message, @"record \d+", "record N"); messages[key] = messages.GetValueOrDefault(key) + 1; }
            }
        }
        Pass($"{built} combinations built and verified");
        foreach (var (message, count) in messages) Info($"refused ×{count}: {message}");
    }

    static void Exports(Session s)
    {
        foreach (var layout in new[] { Layout.PersonTime, Layout.Person })
        {
            var view = Build(s, layout);
            var sas = Export.SasNames(view);
            if (sas.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sas.Count || sas.Any(n => n.Length > 32)) Fail("SAS names " + layout, "not unique or too long"); else Pass($"SAS names {layout}: {sas.Count(n => !view.Fields.Any(f => f.Name == n))} adjusted, all unique ≤ 32");
            foreach (var target in new[] { ExportTarget.R, ExportTarget.Sas })
            {
                string zip = Path.Combine(outRoot, $"bundle-{layout}-{target}.zip"); var watch = Stopwatch.StartNew();
                Export.SaveBundle(zip, view, target);
                Info($"{layout} {target} bundle: {new FileInfo(zip).Length / 1048576.0:0.0} MB in {watch.Elapsed.TotalSeconds:0.0} s");
                if (rscript == null) continue;
                string folder = Path.Combine(outRoot, $"bundle-{layout}-{target}"); if (Directory.Exists(folder)) Directory.Delete(folder, true); ZipFile.ExtractToDirectory(zip, folder);
                int firstNum = view.Fields.FindIndex(f => f.Kind == ValueKind.Number && !f.Key.StartsWith('@'));
                string check = $"source('import.R',encoding='UTF-8')\nstopifnot(nrow(data)=={view.Rows.Count},ncol(data)=={view.Fields.Count})\n" + (firstNum >= 0 ? $"stopifnot(is.numeric(data[[{firstNum + 1}]]))\n" : "") + "cat('ok\\n')\n";
                RScript(folder, check, $"{layout} {target} bundle imports in R with {view.Rows.Count:N0} rows × {view.Fields.Count:N0} columns");
            }
        }
    }

    static void RScript(string folder, string script, string passLabel)
    {
        File.WriteAllText(Path.Combine(folder, "verify.R"), script);
        var start = new ProcessStartInfo(rscript!) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--vanilla"); start.ArgumentList.Add("verify.R"); start.Environment["LC_ALL"] = "C";
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(600000)) { process.Kill(true); Fail(passLabel, "R timed out"); return; }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0) Fail(passLabel, (stdout.Result + stderr.Result).Trim().Split('\n').Last()); else Pass(passLabel + $" ({watch.Elapsed.TotalSeconds:0} s)");
    }

    static ViewPreferences TypicalView(Session s, Layout layout, out View raw)
    {
        raw = Engine.Build(s, layout);
        var prefs = new ViewPreferences(); prefs.Reconcile(raw.Fields.Select(f => f.Key));
        int time = raw.Fields.FindIndex(f => f.Key == "@time");
        if (time >= 0) { prefs.ToggleSort(raw.Fields[time].Key); prefs.MoveSortsToFront(); }
        prefs.ToggleSort(raw.Fields[0].Key); prefs.MoveSortsToFront(); prefs.MoveSort(raw.Fields[0].Key, prefs.Sorts[0].Key); prefs.MoveSortsToFront();
        prefs.Names[raw.Fields[0].Key] = "study_id";
        return prefs;
    }

    static void CodePlans(Session s)
    {
        foreach (var layout in new[] { Layout.PersonTime, Layout.Person })
        {
            var prefs = TypicalView(s, layout, out _);
            var watch = Stopwatch.StartNew();
            var plan = Recipe.Plan(s, layout, MatchMode.AllPeople, false, null, prefs, "");
            string r = Recipe.R(plan, true); string sas = SasRecipe.Generate(plan, true);
            Pass($"{layout} code: R {r.Length / 1024:N0} KB, SAS {sas.Length / 1024:N0} KB in {watch.Elapsed.TotalSeconds:0.0} s");
        }
    }

    // Native R replays use a realistic subset (wide-mapped, role-keyed and timed files with colliding names); the full set is checked above.
    // Choose the files with WIDELONG_STRESS_SUBSET (comma-separated file-name prefixes); by default the first six files are used.
    static Session Subset(Session s)
    {
        var prefixes = (Environment.GetEnvironmentVariable("WIDELONG_STRESS_SUBSET") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chosen = prefixes.Length == 0 ? s.Sources.Take(6) : s.Sources.Where(x => prefixes.Any(p => x.Name.StartsWith(p, StringComparison.Ordinal)));
        return new() { Sources = chosen.Select(x => x.Copy()).ToList() };
    }
    static void NativeR(Session rawAll, Session typedAll)
    {
        var raw = Subset(rawAll); var typed = Subset(typedAll);
        Info("R replay files: " + string.Join(", ", typed.Sources.Select(x => x.Name)));
        void Case(string name, Session s, Layout layout, bool stack = false, RowFilterSpec? rules = null, bool typical = true, string[]? rowNames = null)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                ViewPreferences? prefs = typical && layout != Layout.Measurement ? TypicalView(s, layout, out _) : null;
                RReplay.Run(outRoot, rscript!, name, s, layout, stack, rules: rules, prefs: prefs, rowNames: rowNames, timeoutMs: 600000);
                Pass($"Native R replay {name} ({watch.Elapsed.TotalSeconds:0} s)");
            }
            catch (Exception ex) { Fail("Native R replay " + name, ex.Message.Trim().Split('\n').Last()); }
        }
        Case("raw_long", raw, Layout.PersonTime, typical: false);
        Case("typed_long", typed, Layout.PersonTime);
        Case("typed_wide", typed, Layout.Person);
        var longView = Engine.Build(typed, Layout.PersonTime);
        var entity = longView.Fields.First(f => f.Key == "@entity"); var num = longView.Fields.First(f => f.Kind == ValueKind.Number && !f.Key.StartsWith('@'));
        Case("typed_long_filtered", typed, Layout.PersonTime, rules: new RowFilterSpec { MatchAll = false, Rules = [new(entity.Key, entity.Name, entity.Kind, FilterOperator.Equal, "S"), new(num.Key, num.Name, num.Kind, FilterOperator.IsMissing)] });
        var rowNames = longView.Fields.Where(f => f.Kind == ValueKind.Number && !f.Key.StartsWith('@')).Take(3).Select(f => f.Name).ToArray();
        Case("typed_custom_rows", typed, Layout.Measurement, rowNames: rowNames, typical: false);
        Case("typed_stacked_long", new Session { Sources = typed.Sources.Where(x => x.TimeColumn >= 0).Select(x => x.Copy()).ToList() }, Layout.PersonTime, stack: true, typical: false);
    }
}
