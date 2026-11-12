using WideLong;

/// <summary>0.6.0 features: import presets, saved recipes, value labels and scale scores. Fictional data only.</summary>
public static class FeatureChecks
{
    public const string Qualtrics = "StartDate,ResponseId,Q1,Q2\n\"Start Date\",\"Response ID\",\"How are you?\nPick one\",\"Age\"\n\"{\"\"ImportId\"\":\"\"startDate\"\"}\",\"{\"\"ImportId\"\":\"\"_recordId\"\"}\",\"{\"\"ImportId\"\":\"\"QID1\"\"}\",\"{\"\"ImportId\"\":\"\"QID2\"\"}\"\n2025-01-01,R_1,3,40\n2025-01-02,R_2,1,35\n";
    public static Session QualtricsFixture() { var s = Csv.Import(Qualtrics, "qualtrics_export.csv", ',', null); return new() { Sources = [s] }; }

    /// <summary>Five survey items (one reverse-coded, one with a missing code) and a 2-of-3 minimum rule.</summary>
    public static Session ScaleFixture()
    {
        var s = Csv.Import("id,q1,q2,q3,note\n1,4,2,5,a\n2,1,,99,b\n3,,,3,c\n4,5,5,5,d\n", "survey_items.csv"); s.IdColumn = 0;
        foreach (int c in new[] { 1, 2, 3 }) RegressionChecks.Convert(s, c, ValueKind.Number);
        s.Fields[3].MissingTokens = ["", "99"];
        Scales.Add(s, new ScaleScore { Name = "wellbeing", Items = ["q1", "q2", "q3"], Reversed = ["q2"], ReverseMin = 1, ReverseMax = 5, Method = ScaleMethod.Mean, MinItems = 2 });
        Scales.Add(s, new ScaleScore { Name = "total", Items = ["q1", "q3"], Method = ScaleMethod.Sum, MinItems = 1 });
        return new() { Sources = [s] };
    }

    public static void Run(Action<string, Action> test, Action<bool> assert)
    {
        void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
        bool Rejected(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }

        test("Qualtrics exports are detected and their description rows skipped, here and in R/SAS code", () =>
        {
            var data = QualtricsFixture(); var s = data.Sources[0];
            Equal(2, s.SkipRows); Equal(2, s.Rows.Count); Equal("R_1", s.Rows[0][1]); Equal(1, s.IdColumn);
            Equal(0, Csv.Import(Qualtrics, "plain.csv", ',', 0).SkipRows);
            Equal(0, Csv.Import("id,x\n1,2\n", "plain.csv", ',', null).SkipRows);
            var plan = Recipe.Plan(data, Layout.PersonTime, MatchMode.AllPeople, false, null, new ViewPreferences(), "");
            assert(Recipe.R(plan).Contains("[-seq_len(2), , drop = FALSE]", StringComparison.Ordinal));
            assert(Rejected(() => SasRecipe.Generate(plan))); // the question text has a line break
            var simple = Csv.Import("ResponseId,Q1\n\"Response ID\",\"Q one\"\n\"{\"\"ImportId\"\":\"\"_recordId\"\"}\",\"{\"\"ImportId\"\":\"\"QID1\"\"}\"\nR_1,3\n", "simple.csv", ',', null);
            assert(SasRecipe.Generate(Recipe.Plan(new Session { Sources = [simple] }, Layout.PersonTime, MatchMode.AllPeople, false, null, new ViewPreferences(), "")).Contains("_dh_skip", StringComparison.Ordinal));
            assert(Rejected(() => Csv.Import("id,x\n1,2\n", "short.csv", ',', 2)));
        });
        test("Scale scores reverse-code items, respect missing codes and the minimum-items rule", () =>
        {
            var data = ScaleFixture(); var s = data.Sources[0];
            Equal(7, s.Fields.Count); Equal(5, s.FileColumns); Equal(5, s.OriginalRows![0].Length);
            // id 1: q1=4, q2 reversed 6-2=4, q3=5 → 13/3; id 2: q1=1, q2 blank, q3 99=missing → one item < 2 → missing; id 4: 5, 1, 5
            Equal("4.33333333333333", s.Rows[0][5]); Equal("", s.Rows[1][5]); Equal("", s.Rows[2][5]); Equal("3.66666666666667", s.Rows[3][5]);
            Equal("9", s.Rows[0][6]); Equal("1", s.Rows[1][6]); Equal("3", s.Rows[2][6]);
            var view = Engine.Build(data, Layout.PersonTime); assert(view.Fields.Any(f => f.Name == "wellbeing" && f.Kind == ValueKind.Number));
            Equal(true, Recipe.Replay(s)[0].SequenceEqual(s.Rows[0]));
            var plan = Recipe.Plan(data, Layout.PersonTime, MatchMode.AllPeople, false, null, new ViewPreferences(), "");
            assert(Recipe.R(plan).Contains("rowMeans(items, na.rm = TRUE)", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(SasRecipe.Generate(plan), @"if n\(q1\w*, 6 - q2\w*, q3\w*\) >= 2 then \w+ = mean\("));
            assert(Rejected(() => Scales.Add(s, new ScaleScore { Name = "bad", Items = ["q1", "note"] })));
            assert(Rejected(() => Scales.Add(s, new ScaleScore { Name = "wellbeing", Items = ["q1", "q3"] })));
            var copy = s.Copy(); Scales.Remove(copy, copy.Scales[0]); Equal(6, copy.Fields.Count); Equal("9", copy.Rows[0][5]); Equal(2, s.Scales.Count);
        });
        test("Value labels travel to the dictionary, R factors and SAS formats", () =>
        {
            var s = Csv.Import("id,agree,color\n1,1,r\n2,2,g\n3,,r\n", "labels.csv"); RegressionChecks.Convert(s, 1, ValueKind.Number);
            s.Fields[1].ValueLabels = new() { ["1"] = "Disagree", ["2"] = "Agree" }; s.Fields[2].ValueLabels = new() { ["r"] = "Red" };
            var view = Engine.SourceView(s); Equal("Disagree", view.Fields[1].Labels!["1"]);
            var files = new Dictionary<string, string>();
            string zip = Path.Combine(Path.GetTempPath(), "widelong-labels-" + Guid.NewGuid().ToString("N") + ".zip");
            Export.SaveBundles(zip, new Dictionary<string, View> { [""] = view }, ExportTarget.R, "labelled");
            using (var archive = System.IO.Compression.ZipFile.OpenRead(zip)) foreach (var entry in archive.Entries) { using var reader = new StreamReader(entry.Open()); files[entry.Name] = reader.ReadToEnd(); }
            File.Delete(zip);
            assert(files["labelled dictionary.csv"].Contains("1 = Disagree; 2 = Agree", StringComparison.Ordinal));
            assert(files["labelled import.R"].Contains("factor(data[[2]], levels = c(1, 2), labels = c(\"Disagree\", \"Agree\"))", StringComparison.Ordinal));
            assert(files["labelled import.R"].Contains("attr(data[[3]], \"labels\")", StringComparison.Ordinal)); // g has no label, so the values stay
            var (code, formats) = Export.SasFormats(view.Fields); assert(code.Contains("value wla 1 = 'Disagree' 2 = 'Agree';", StringComparison.Ordinal) && formats[2] == "$wlb");
            var plan = Recipe.Plan(new Session { Sources = [s] }, Layout.Source, MatchMode.AllPeople, false, s.Key, new ViewPreferences(), "");
            assert(Recipe.R(plan).Contains("attr(prepared$agree, \"labels\") <- c(\"Disagree\" = 1, \"Agree\" = 2)", StringComparison.Ordinal));
            assert(SasRecipe.Generate(plan).Contains("format agree wla.", StringComparison.Ordinal));
        });
        test("Recipes save settings and steps without data and rebuild the same session from the originals", () =>
        {
            var data = ScaleFixture(); var s = data.Sources[0];
            s.KeepRules.Add(new(4, ["a", "b", "d"], false)); s.Fields[4].ValueLabels = new() { ["a"] = "Alpha" }; data.TimeOrder = ["x"];
            s.CaptureOriginal(); s.Steps.Add(new(StepKind.Cell, 4, 2, "c", "cc")); s.Rows[2][4] = "cc";
            var prefs = new Dictionary<string, ViewPreferences> { ["PersonTime:0"] = new() { Sorts = [new("@person_id", true)] } };
            string json = SessionRecipes.Serialize(SessionRecipes.Capture(data, Layout.Person, MatchMode.CommonPeople, false, s.Key, prefs, "find", new RowFilterSpec { MatchAll = false }));
            assert(!json.Contains("\"Rows\": [", StringComparison.Ordinal) && !json.Contains("4.33", StringComparison.Ordinal));
            var recipe = SessionRecipes.Parse(json);
            Equal(Layout.Person, recipe.Layout); Equal(MatchMode.CommonPeople, recipe.Match); Equal(true, recipe.Preferences["PersonTime:0"].Sorts[0].Descending);
            string original = "id,q1,q2,q3,note\n1,4,2,5,a\n2,1,,99,b\n3,,,3,c\n4,5,5,5,d\n";
            var (rebuilt, notes) = SessionRecipes.Open(recipe, _ => (original, null));
            var r = rebuilt.Sources[0]; Equal(0, notes.Count);
            Equal(string.Join(";", s.Rows.Select(x => string.Join("|", x))), string.Join(";", r.Rows.Select(x => string.Join("|", x))));
            Equal(s.Key, r.Key); Equal(2, r.Scales.Count); Equal("Alpha", r.Fields[4].ValueLabels!["a"]); Equal(ValueKind.Number, r.Fields[1].Kind); Equal("x", rebuilt.TimeOrder[0]);
            Equal(Engine.Build(data, Layout.PersonTime).Rows.Count, Engine.Build(rebuilt, Layout.PersonTime).Rows.Count);
            assert(Rejected(() => SessionRecipes.Open(recipe, _ => ("id,q1,q2,q3,other\n1,4,2,5,a\n", null))));
            assert(Rejected(() => SessionRecipes.Open(recipe, _ => ("id,q1,q2,q3,note\n1,4,2,5,a\n2,1,,99,b\n3,,,3,changed\n", null))));
            var (grown, more) = SessionRecipes.Open(recipe, _ => (original + "5,1,1,1,e\n", null)); Equal(5, grown.Sources[0].Rows.Count); Equal(1, more.Count);
            assert(Rejected(() => SessionRecipes.Parse("{\"Format\":\"something else\"}")) && Rejected(() => SessionRecipes.Parse("not json")));
        });
        test("REDCap exports suggest record_id and redcap_event_name", () =>
        {
            var s = Csv.Import("record_id,redcap_event_name,score\n1,baseline_arm_1,5\n1,month_3_arm_1,6\n2,baseline_arm_1,7\n", "redcap.csv");
            Equal(0, s.IdColumn); Equal(1, s.TimeColumn); assert(ImportPresets.LooksLikeRedcap(s));
            var fresh = Csv.Import("record_id,redcap_event_name,score\n1,baseline_arm_1,5\n1,month_3_arm_1,6\n", "redcap2.csv"); fresh.IdChosen = false; fresh.GroupChosen = false; fresh.EntityChosen = false; fresh.TimeColumn = -1;
            KeyRecommendations.Recommend(new Session { Sources = [fresh] }); Equal(1, fresh.TimeColumn);
        });
    }
}
