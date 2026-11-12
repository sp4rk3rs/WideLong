using WideLong;

/// <summary>Regressions for the 0.6.0 review: key conversions in generated code, Replace on filtered variables, partial key conversions,
/// two-digit years, shared originals, key filters for later files, and wide time ordering. Fictional data only.</summary>
public static class RegressionChecks
{
    /// <summary>Does what Change variable type does: records the step, rewrites values, and keeps a response filter on the same records.</summary>
    public static void Convert(Source s, int column, ValueKind target, string format = "yyyy-MM-dd")
    {
        s.CaptureOriginal(); var tokens = s.EffectiveMissingTokens(column).ToArray();
        s.Steps.Add(new(StepKind.Convert, column, Target: target, DateFormat: format, MissingTokens: tokens));
        int rule = s.KeepRules.FindIndex(r => r.Column == column);
        if (rule >= 0) s.KeepRules[rule] = s.KeepRules[rule] with { Values = s.KeepRules[rule].Values.Select(v => Values.ConvertWithMissing(v, target, format, tokens)).Distinct().ToArray() };
        foreach (var row in s.Rows) row[column] = Values.ConvertWithMissing(row[column], target, format, tokens);
        s.Fields[column].Kind = target;
    }
    public static void Replace(Source s, int column, string before, string after)
    {
        s.CaptureOriginal(); s.Steps.Add(new(StepKind.Replace, column, Before: before, After: after));
        foreach (var row in s.Rows) if (row[column] == before) row[column] = after;
        ResponseFilters.FollowReplace(s, column, before, after);
    }
    public static Session DateKeyFixture()
    {
        var visits = Csv.Import("id,visit_date,x\n1,3/5/2025,a\n1,4/5/2025,b\n2,3/6/2025,c\n", "date_visits.csv");
        visits.IdColumn = 0; visits.TimeColumn = 1;
        Convert(visits, 1, ValueKind.Date, "M/d/yyyy");
        var labs = Csv.Import("id,visit_date,lab\n1,2025-03-05,10\n2,2025-03-06,20\n", "date_labs.csv");
        labs.IdColumn = 0; labs.TimeColumn = 1;
        Convert(labs, 1, ValueKind.Date);
        return new() { Sources = [visits, labs] };
    }
    public static Session PaddedTimeFixture()
    {
        var s = Csv.Import("id,wave,score\nA,01,5\nA,02,6\nB,01,7\n", "padded_waves.csv"); s.IdColumn = 0; s.TimeColumn = 1;
        Convert(s, 1, ValueKind.Number);
        return new() { Sources = [s] };
    }
    public static Session FilteredKeyFixture()
    {
        var s = Csv.Import("id,wave,score\nA,01,5\nA,02,6\nB,01,7\n", "filtered_waves.csv"); s.IdColumn = 0; s.TimeColumn = 1;
        s.KeepRules.Add(new(1, ["01"], false));
        Convert(s, 1, ValueKind.Number);
        return new() { Sources = [s] };
    }
    public static Session PartialIdFixture()
    {
        var a = Csv.Import("id,age\n00001,30\n00002,40\n", "partial_a.csv"); a.IdColumn = 0;
        Convert(a, 0, ValueKind.Number);
        var b = Csv.Import("id,score\n00001,5\n00002,6\n", "partial_b.csv"); b.IdColumn = 0;
        return new() { Sources = [a, b] };
    }
    public static Session ReplacedFilterFixture()
    {
        var s = Csv.Import("id,person,score\n1,Patient,5\n1,Spouse,6\n1,Informant,7\n", "dyad_roles.csv"); s.IdColumn = 0; s.EntityColumn = 1;
        s.KeepRules.Add(new(1, ["Patient", "Spouse"], false));
        Replace(s, 1, "Spouse", "Caregiver");
        return new() { Sources = [s] };
    }

    public static void Run(Action<string, Action> test, Action<bool> assert)
    {
        void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
        bool Rejected(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }

        test("Date and padded-number time keys are rewritten in generated R/SAS code before combining", () =>
        {
            foreach (var layout in new[] { Layout.PersonTime, Layout.Person })
            {
                var data = DateKeyFixture();
                var view = Engine.Build(data, layout);
                Equal(layout == Layout.PersonTime ? 3 : 2, view.Rows.Count);
                var plan = Recipe.Plan(data, layout, MatchMode.AllPeople, false, null, new ViewPreferences(), "");
                string r = Recipe.R(plan), sas = SasRecipe.Generate(plan);
                assert(r.Contains("format(as.Date(", StringComparison.Ordinal) && r.Contains("\"%m/%d/%Y\"", StringComparison.Ordinal));
                assert(sas.Contains("put(input(", StringComparison.Ordinal));
            }
            var padded = PaddedTimeFixture();
            var wide = Engine.Build(padded, Layout.Person);
            assert(wide.Fields.Any(f => f.Name == "score__1") && wide.Fields.Any(f => f.Name == "score__2"));
            string code = Recipe.R(Recipe.Plan(padded, Layout.Person, MatchMode.AllPeople, false, null, new ViewPreferences(), ""));
            assert(code.Contains("as_text(as.numeric(", StringComparison.Ordinal));
        });
        test("Response filters on converted keys follow the canonical value in generated code", () =>
        {
            var data = FilteredKeyFixture();
            Equal(2, Engine.Build(data, Layout.PersonTime).Rows.Count);
            string r = Recipe.R(Recipe.Plan(data, Layout.PersonTime, MatchMode.AllPeople, false, null, new ViewPreferences(), ""));
            assert(r.IndexOf("as_text(as.numeric(", StringComparison.Ordinal) < r.IndexOf("%in% \"1\"", StringComparison.Ordinal));
        });
        test("Replace values keeps records that a response filter kept", () =>
        {
            var data = ReplacedFilterFixture(); var s = data.Sources[0];
            Equal(true, s.KeepRules[0].Values.Contains("Caregiver"));
            var view = Engine.Build(data, Layout.PersonTime);
            Equal("Patient,Caregiver", string.Join(",", view.Rows.Select(row => row[1].Value)));
            // A replacement of an excluded response does not bring it back, and replacing with a missing value keeps the record.
            var t = Csv.Import("id,status\n1,Done\n2,Skip\n3,Done\n", "status.csv"); t.KeepRules.Add(new(1, ["Done"], false));
            Replace(t, 1, "Skip", "Later"); Equal(false, t.KeepRules[0].Values.Contains("Later"));
            Replace(t, 1, "Done", ""); Equal(true, t.KeepRules[0].KeepMissing); Equal(2, Engine.SourceView(t).Rows.Count);
        });
        test("Converting an ID in only some files is reported when it breaks matching", () =>
        {
            var data = PartialIdFixture();
            var view = Engine.Build(data, Layout.PersonTime);
            Equal(4, view.Rows.Count); // 1, 2 (converted) and 00001, 00002 (unconverted) no longer match
            assert(view.Notes[0].Contains("00001", StringComparison.Ordinal) && Engine.KeyMatchBreak(data) != null);
            Convert(data.Sources[1], 0, ValueKind.Number);
            Equal(null, Engine.KeyMatchBreak(data)); Equal(2, Engine.Build(data, Layout.PersonTime).Rows.Count);
        });
        test("Two-digit years are refused and need a four-digit year pattern", () =>
        {
            assert(Values.DateFormatProblem("MM/dd/yy") != null && Values.DateFormatProblem("yy-M-d") != null && Values.DateFormatProblem("dd.MM") != null);
            Equal(null, Values.DateFormatProblem("M/d/yyyy"));
            assert(Rejected(() => Values.Convert("05/12/55", ValueKind.Date, "MM/dd/yy")));
            var s = Csv.Import("id,d\n1,05/12/55\n", "years.csv"); Equal(1, Values.Failures(s, 1, ValueKind.Date, "MM/dd/yy").Count);
        });
        test("Undo snapshots share the unchanged original rows", () =>
        {
            var data = Examples.All[0].Create(); var copy = data.Copy();
            assert(data.Sources.Zip(copy.Sources).All(p => ReferenceEquals(p.First.OriginalRows, p.Second.OriginalRows)));
            copy.Sources[0].Rows[0][1] = "changed"; Equal(false, data.Sources[0].Rows[0][1] == "changed");
        });
        test("Wide time columns follow the within-ID order, and an explicit order wins", () =>
        {
            var s = Csv.Import("id,visit,x\n1,Month 3,a\n1,Month 6,b\n2,Baseline,c\n2,Month 3,d\n2,Month 6,e\n", "late_baseline.csv"); s.IdColumn = 0; s.TimeColumn = 1;
            var data = new Session { Sources = [s] };
            Equal("x__Baseline,x__Month 3,x__Month 6", string.Join(",", Engine.Build(data, Layout.Person).Fields.Skip(1).Select(f => f.Name)));
            data.TimeOrder = ["Month 6", "Baseline"];
            Equal("x__Month 6,x__Baseline,x__Month 3", string.Join(",", Engine.Build(data, Layout.Person).Fields.Skip(1).Select(f => f.Name)));
            Equal("Month 6,Baseline,Month 3", string.Join(",", Engine.TimeValues(data)));
            Equal(true, data.Copy().TimeOrder.SequenceEqual(data.TimeOrder));
        });
        test("Cleaning code reads the real file when the same file is imported twice", () =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "widelong-dup-" + Guid.NewGuid().ToString("N"));
            var a = Csv.Import("id,x\n1,a\n", "data.csv"); a.ImportPath = Path.Combine(folder, "data.csv");
            var b = Csv.Import("id,y\n1,b\n", "data.csv"); b.Name = "data (2).csv"; b.ImportPath = Path.Combine(folder, "data.csv");
            var plan = Recipe.Plan(new Session { Sources = [a, b] }, Layout.PersonTime, MatchMode.AllPeople, false, null, new ViewPreferences(), "");
            string r = Recipe.R(plan), sas = SasRecipe.Generate(plan);
            assert(!r.Contains("data (2).csv", StringComparison.Ordinal) && !sas.Contains("data (2).csv", StringComparison.Ordinal));
            var odd = Csv.Import("id,x\n1,a\n", "R&D;100%.csv"); odd.ImportPath = Path.Combine(folder, "R&D;100%.csv");
            string oddSas = SasRecipe.Generate(Recipe.Plan(new Session { Sources = [odd] }, Layout.PersonTime, MatchMode.AllPeople, false, null, new ViewPreferences(), ""));
            assert(!oddSas.Contains("%let folder", StringComparison.Ordinal) && oddSas.Contains("infile '", StringComparison.Ordinal));
        });
        test("Response filters on a matching key extend to files imported later", () =>
        {
            var a = Csv.Import("id,person,x\n1,Patient,a\n1,Informant,b\n", "roles_a.csv"); a.IdColumn = 0; a.EntityColumn = 1; a.KeepRules.Add(new(1, ["Patient"], false));
            var b = Csv.Import("id,person,y\n1,Patient,c\n1,Informant,d\n", "roles_b.csv"); b.IdColumn = 0; b.EntityColumn = 1;
            var data = new Session { Sources = [a, b] };
            Equal(1, ResponseFilters.ExtendKeyFilters(data, [b]));
            Equal(1, Engine.Build(data, Layout.PersonTime).Rows.Count);
            Equal(0, ResponseFilters.ExtendKeyFilters(data, [b]));
        });
    }
}
