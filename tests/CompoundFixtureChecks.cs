using WideLong;

public static class CompoundFixtureChecks
{
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    private static int Field(View view, string key) { int c = view.Fields.FindIndex(f => f.Key == key); return c >= 0 ? c : throw new Exception("Missing output key " + key); }
    private static int Named(View view, string name) { int c = view.Fields.FindIndex(f => f.Name == name); return c >= 0 ? c : throw new Exception("Missing output field " + name); }
    private static Cell[] LongRow(View view, string id, string person, string time)
    {
        int i = Field(view, "@person_id"), p = Field(view, "@entity"), t = Field(view, "@time");
        return view.Rows.Single(r => r[i].Value == id && r[p].Value == person && r[t].Value == time);
    }

    public static void Run(Action<string, Action> test)
    {
        test("Sixth fictional example documents and preserves ID/person/time source roles", () =>
        {
            Equal(6, Examples.All.Count);
            var example = Examples.All.Single(e => e.Title == "Household · roles and repeated visits");
            Equal(true, example.Description.Contains("ID + person + time", StringComparison.Ordinal));
            var session = example.Create(); Equal(5, session.Sources.Count); Equal(true, Examples.All.All(e => e.Create().Sources.Count >= 5));
            Equal(4, session.Sources[0].Rows.Count); Equal(26, session.Sources[1].Rows.Count); Equal(8, session.Sources[2].Rows.Count);
            Equal(-1, session.Sources[0].EntityColumn); Equal(1, session.Sources[1].EntityColumn); Equal(1, session.Sources[2].EntityColumn);
            Equal(2, session.Sources[1].TimeColumn); Equal(-1, session.Sources[2].TimeColumn);
            Equal(true, session.Sources.All(s => s.IdChosen && s.EntityChosen && s.GroupChosen && s.OriginalRows != null));
            Equal(6, session.Sources[2].Fields.Count(f => f.Measure.Length > 0));
            Equal(true, session.Sources.All(s => s.ImportPath == null));
        });
        test("Household example matches 31 observed triples and broadcasts ID-only context", () =>
        {
            var session = Examples.CompoundFixture(); var view = Engine.Build(session, Layout.PersonTime);
            Equal(31, view.Rows.Count); Equal(9, view.Fields.Count);
            int id = Field(view, "@person_id"), person = Field(view, "@entity"), time = Field(view, "@time");
            Equal("household_id", view.Fields[id].Name); Equal("person", view.Fields[person].Name); Equal("time", view.Fields[time].Name);
            Equal(31, view.Rows.Select(r => (r[id].Value, r[person].Value, r[time].Value)).Distinct().Count());
            Equal(true, view.Rows.All(r => r[Named(view, "household_name")].Value == "Household " + int.Parse(r[id].Value[1..])));
            Equal("10", LongRow(view, "H001", "Mother", "Baseline")[Named(view, "feedback_score")].Value);
            Equal("13", LongRow(view, "H001", "Father", "Baseline")[Named(view, "feedback_score")].Value);
            Equal("16", LongRow(view, "H001", "Child", "Baseline")[Named(view, "feedback_score")].Value);
            Equal("56", LongRow(view, "H001", "Mother", "Month 3")[Named(view, "confidence")].Value);
            Equal("58", LongRow(view, "H001", "Father", "Month 3")[Named(view, "confidence")].Value);
        });
        test("Missing Child follow-ups are not invented from other household roles", () =>
        {
            var view = Engine.Build(Examples.CompoundFixture(), Layout.PersonTime);
            int id = Field(view, "@person_id"), person = Field(view, "@entity"), time = Field(view, "@time");
            string Times(string household) => string.Join(",", view.Rows.Where(r => r[id].Value == household && r[person].Value == "Child").Select(r => r[time].Value));
            Equal("Baseline", Times("H001")); Equal("Baseline,Month 6", Times("H002")); Equal("Baseline,Month 3,Month 6", Times("H003")); Equal("Baseline", Times("H004"));
            var informant = LongRow(view, "H002", "Child", "Month 6");
            Equal("28", informant[Named(view, "feedback_score")].Value); Equal(true, informant[Named(view, "confidence")].Missing); Equal(true, informant[Named(view, "confidence")].Origin == null);
            var wideOnlyFollowup = LongRow(view, "H003", "Mother", "Month 3");
            Equal("66", wideOnlyFollowup[Named(view, "confidence")].Value); Equal(true, wideOnlyFollowup[Named(view, "feedback_score")].Missing);
        });
        test("Compound Wide keeps one row per ID with separate role/time columns", () =>
        {
            var view = Engine.Build(Examples.CompoundFixture(), Layout.Person); Equal(4, view.Rows.Count); Equal(33, view.Fields.Count);
            Equal(false, view.Fields.Any(f => f.Key == "@entity"));
            Equal(false, view.Fields.Any(f => f.Name.StartsWith("confidence__Child__", StringComparison.Ordinal) || f.Name.StartsWith("focus__Child__", StringComparison.Ordinal)));
            int id = Field(view, "@person_id"); var first = view.Rows.Single(r => r[id].Value == "H001");
            Equal("Household 1", first[Named(view, "household_name")].Value);
            Equal("56", first[Named(view, "confidence__Mother__Month 3")].Value); Equal("58", first[Named(view, "confidence__Father__Month 3")].Value);
            Equal("10", first[Named(view, "feedback_score__Mother__Baseline")].Value); Equal("13", first[Named(view, "feedback_score__Father__Baseline")].Value);
            Equal(true, first[Named(view, "feedback_score__Child__Month 3")].Missing);
            var second = view.Rows.Single(r => r[id].Value == "H002"); Equal(true, second[Named(view, "confidence__Father__Month 3")].Missing);
        });
        test("Compound layouts retain genuine missing scores and correct source provenance", () =>
        {
            var session = Examples.CompoundFixture(); var view = Engine.Build(session, Layout.PersonTime);
            var missingQuestionnaire = LongRow(view, "H003", "Child", "Month 3")[Named(view, "feedback_score")];
            Equal(true, missingQuestionnaire.Missing); Equal(session.Sources[1].Key, missingQuestionnaire.Origin!.SourceKey);
            var missingWide = LongRow(view, "H002", "Father", "Month 3")[Named(view, "confidence")];
            Equal(true, missingWide.Missing); Equal(session.Sources[2].Key, missingWide.Origin!.SourceKey);
            var patient = LongRow(view, "H001", "Mother", "Baseline")[Named(view, "confidence")].Origin!;
            var spouse = LongRow(view, "H001", "Father", "Baseline")[Named(view, "confidence")].Origin!;
            Equal(session.Sources[2].Key, patient.SourceKey); Equal(session.Sources[2].Key, spouse.SourceKey); Equal(false, patient.Row == spouse.Row);
            Equal("Mother", session.Sources[2].Rows[patient.Row][1]); Equal("Father", session.Sources[2].Rows[spouse.Row][1]);
        });
        test("Compound custom rows stack selected cells without adding absent-role values", () =>
        {
            var session = Examples.CompoundFixture(); var selected = Engine.SuggestedRowVariables(session);
            Equal(2, selected.Count); var view = Engine.Build(session, Layout.Measurement, rowVariables: selected);
            Equal(48, view.Rows.Count); Equal(10, view.Fields.Count);
            int person = Field(view, "@entity"), value = Field(view, "@value"), variable = Field(view, "@variable");
            Equal(true, view.Rows.All(r => r[person].Value is "Mother" or "Father"));
            Equal(2, view.Rows.Count(r => r[value].Missing)); Equal(ValueKind.Number, view.Fields[value].Kind);
            Equal(true, view.Rows.All(r => r[variable].Value is "confidence" or "focus"));
            Equal(true, view.Rows.All(r => r[value].Origin?.SourceKey == session.Sources[2].Key));
            Equal(true, view.Rows.All(r => !r[Named(view, "household_name")].Missing));
        });
    }
}
