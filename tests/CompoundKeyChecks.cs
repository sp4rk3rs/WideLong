using WideLong;

public static class CompoundKeyChecks
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static Session With(params Source[] sources) => new() { Sources = sources.ToList() };
    private static Source Roles(string csv, string name, int time = 2)
    {
        var source = Csv.Import(csv, name); source.IdColumn = 0; source.EntityColumn = 1; source.TimeColumn = time; source.EntityChosen = true; return source;
    }
    private static void Refuses(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected invalid compound keys to be rejected.");
    }
    private static int Column(View view, string key) => view.Fields.FindIndex(f => f.Key == key);
    public static void Run(Action<string, Action> test)
    {
        test("Compound ID/entity/time keys accept several people and reject full-key duplicates or missing entities", () =>
        {
            var source = Roles("id,person,time,value\nH1,Patient,m0,10\nH1,Spouse,m0,20\nH1,Patient,m3,11", "roles.csv");
            var view = Engine.Build(With(source), Layout.PersonTime);
            Require(view.Rows.Count == 3 && view.Fields[1].Key == "@entity" && view.Fields[2].Key == "@time", "Additional key must distinguish same ID/time records and appear between ID/time.");
            var duplicate = source.Copy(); duplicate.Rows.Add(["H1", "Patient", "m0", "99"]); Refuses(() => Engine.Build(With(duplicate), Layout.PersonTime));
            var missing = source.Copy(); missing.Rows[1][1] = ""; Refuses(() => Engine.Build(With(missing), Layout.PersonTime));
            var invalid = source.Copy(); invalid.EntityColumn = invalid.TimeColumn; Refuses(() => Engine.Build(With(invalid), Layout.PersonTime));
            var copy = source.Copy(); Require(copy.EntityColumn == 1 && copy.EntityChosen && With(copy).HasEntity, "Snapshots must retain compound key configuration.");
        });
        test("Compound matching broadcasts ID values and scopes role-wide values and observation times", () =>
        {
            var household = Csv.Import("id,city\nH1,Portland", "household.csv");
            var wide = Roles("id,person,nickname,score_m0,score_m3\nH1,Patient,Pat,10,11\nH1,Spouse,Sue,,21", "scores.csv", -1);
            var repeated = Roles("id,person,time,weight\nH1,Patient,m0,60\nH1,Patient,m3,61\nH1,Informant,m0,70", "long.csv");
            var session = With(household, wide, repeated); var view = Engine.Build(session, Layout.PersonTime);
            Require(view.Rows.Count == 5, "Patient and spouse need two rows each; the informant only has its observed m0 row.");
            int entity = Column(view, "@entity"), time = Column(view, "@time"), city = Column(view, household.Key + ":1"), nickname = Column(view, wide.Key + ":2");
            Require(view.Rows.All(r => r[city].Value == "Portland"), "ID-only fields must broadcast to every entity.");
            Require(view.Rows.Where(r => r[entity].Value == "Spouse").All(r => r[nickname].Value == "Sue"), "Entity-specific static values must not come from the first entity.");
            Require(view.Rows.Count(r => r[entity].Value == "Informant") == 1 && view.Rows.Single(r => r[entity].Value == "Informant")[time].Value == "m0", "Unrelated roles' times must not create phantom informant rows.");
            var result = Engine.Build(session, Layout.Person);
            Require(result.Rows.Count == 1 && result.Fields.All(f => f.Key != "@entity"), "Wide output must keep one row per ID and expand entity-qualified columns.");
            Require(result.Fields.Any(f => f.Name == "score__Patient__m3") && result.Fields.Any(f => f.Name == "nickname__Spouse"), "Wide role-specific static/timed fields need clear suffixes.");
            Require(!result.Fields.Any(f => f.Name.StartsWith("score__Informant", StringComparison.Ordinal)), "A role absent from the score source must not create ghost score columns.");
            int missing = result.Fields.FindIndex(f => f.Name == "score__Spouse__m0"); Require(missing >= 0 && result.Rows[0][missing].Missing, "Selected source columns with missing values must remain in the schema.");
            int patient = result.Fields.FindIndex(f => f.Name == "score__Patient__m3"); var origin = result.Rows[0][patient].Origin;
            Require(result.Rows[0][patient].Value == "11" && origin?.SourceKey == wide.Key && origin.Row == 0 && origin.Column == 4, "Reshaped values must retain original source-cell provenance.");
            Require(result.Fields[patient].Key == wide.Key + ":measure:score\u001fentity:Patient\u001ftime:m3", "Entity qualifier must precede time in the stable output key.");
            wide.Fields[4].Keep = false; var selected = Engine.Build(session, Layout.Person);
            Require(selected.Fields.All(f => !f.Name.StartsWith("score__", StringComparison.Ordinal) || !f.Name.EndsWith("__m3", StringComparison.Ordinal)), "Deselected wide occasions must not reappear for any entity.");
        });
        test("Entity-free timed observations apply to every entity at the same time only", () =>
        {
            var roles = Roles("id,person,time,score\nH1,Patient,m0,10\nH1,Spouse,m3,20", "roles.csv");
            var shared = Csv.Import("id,time,weather\nH1,m0,Sunny", "weather.csv");
            var view = Engine.Build(With(roles, shared), Layout.PersonTime); int entity = Column(view, "@entity"), time = Column(view, "@time"), score = Column(view, roles.Key + ":3"), weather = Column(view, shared.Key + ":2");
            Require(view.Rows.Count == 3, "Shared m0 applies to both entities, without adding Patient m3 from the spouse's role-specific record.");
            Require(view.Rows.Where(r => r[time].Value == "m0").All(r => r[weather].Value == "Sunny"), "Entity-free m0 values should broadcast to matching times.");
            var spouseM0 = view.Rows.Single(r => r[entity].Value == "Spouse" && r[time].Value == "m0"); Require(spouseM0[score].Missing, "Role-specific scores must not cross into another role's broadcast row.");
            Require(view.Rows.Single(r => r[time].Value == "m3")[weather].Missing, "A time-specific shared value must not broadcast to a different time.");
        });
        test("Stacking wide role files retains each role's baseline and source provenance", () =>
        {
            var a = Roles("id,person,name,score_m0,score_m3\nH1,Patient,Pat,10,11\nH1,Spouse,Sue,20,21", "first.csv", -1);
            var b = Roles("id,person,name,score_m0,score_m3\nH1,Informant,Ian,30,31", "second.csv", -1);
            var view = Engine.Build(With(a, b), Layout.PersonTime, stack: true);
            int entity = Column(view, "@entity"), name = Column(view, "stack:name"), provenance = view.Fields.FindIndex(f => f.Source == "provenance");
            Require(view.Rows.Count == 6 && view.Rows.Where(r => r[entity].Value == "Spouse").All(r => r[name].Value == "Sue"), "Stacking must find the baseline for each entity rather than using the first ID baseline.");
            Require(view.Rows.Where(r => r[entity].Value == "Informant").All(r => r[provenance].Value == "second.csv"), "Stacked observations must retain their independent file source.");
        });
        test("Compound diagnostics and mapping table recognize the additional key", () =>
        {
            var source = Roles("id,person,time,value\nH1,Patient,m0,10\nH1,Spouse,m0,20\nH1,,m0,30", "keys.csv");
            var report = MatchingDiagnostics.Analyze(With(source)).Single();
            Require(report.DistinctKeys == 2 && report.DuplicateKeys == 0 && report.MissingEntityRows == 1 && report.MissingKeyRows == 1, "Diagnostics must distinguish additional-key values and count incomplete roles.");
            Require(report.Issues.Single().Kind == MatchIssueKind.MissingEntity && report.Issues.Single().Column == 1, "Missing additional-key diagnostics must navigate to that source column.");
            Require(RepeatedColumnMapping.FromSource(source).All(m => m.Column == 3), "The additional key must not be offered as a repeated value column.");
            source.Rows[2][1] = "Patient"; var duplicate = MatchingDiagnostics.Analyze(With(source)).Single();
            Require(duplicate.DuplicateKeys == 1 && duplicate.DuplicateRows == 2 && duplicate.Issues.Where(i => i.Kind == MatchIssueKind.DuplicateKey).All(i => i.Entity == "Patient"), "Diagnostic duplicate issues must retain the full key including the entity value.");
        });
    }
}
