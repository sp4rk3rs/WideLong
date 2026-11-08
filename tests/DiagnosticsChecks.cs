using WideLong;

public static class DiagnosticsChecks
{
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run(Action<string, Action> test)
    {
        test("Matching diagnostics report every missing and duplicate key record", () =>
        {
            var source = Csv.Import("id,visit,value\nA,m0,1\nA,m0,2\nB,,3\n,m0,4\nC,m1,5", "keys.csv");
            source.IdColumn = 0; source.TimeColumn = 1;
            var report = MatchingDiagnostics.Analyze(new() { Sources = [source] }).Single();
            Require(report.DistinctIds == 2 && report.DistinctKeys == 2, "Only complete keys should contribute to matching coverage.");
            Require(report.MissingIdRows == 1 && report.MissingGroupingRows == 1 && report.MissingKeyRows == 2, "Missing-key counts should identify all affected records.");
            Require(report.DuplicateKeys == 1 && report.DuplicateRows == 2 && report.ExtraDuplicateRows == 1, "Duplicate groups, all members and excess records should be counted separately.");
            Require(report.Issues.Count(i => i.Kind == MatchIssueKind.DuplicateKey) == 2, "Both duplicate records must be inspectable.");
        });
        test("Matching diagnostics use exact complete IDs and expose unmatched source origins", () =>
        {
            var a = Csv.Import("id,visit,value\nA,m0,1\nB,m0,2\nC,,3", "first.csv");
            var b = Csv.Import("id,visit,value\nA,m0,1\nb,m0,2\nC,m0,3", "second.csv");
            var reports = MatchingDiagnostics.Analyze(new() { Sources = [a, b] });
            Require(reports[0].SharedIds.Single().Shared == 1 && reports[0].SharedIds.Single().Coverage == "50%", "Coverage must be exact/case-sensitive and omit incomplete grouping keys.");
            var issue = reports[0].Issues.Single(i => i.Kind == MatchIssueKind.UnmatchedId);
            Require(issue.Id == "B" && issue.Row == 1 && issue.Record == 2 && issue.Column == 0 && issue.OtherSourceKey == b.Key, "Unmatched issues must retain original source record and column.");
        });
        test("Matching diagnostics remain available when no ID has been configured", () =>
        {
            var source = Csv.Import("code,value\nX,1\nY,2", "unknown.csv"); source.IdColumn = -1;
            var report = MatchingDiagnostics.Analyze(new() { Sources = [source] }).Single();
            Require(!report.Configured && report.KeyStatus == "Choose keys" && report.Issues.Single().Kind == MatchIssueKind.UnconfiguredKey, "Unconfigured files should produce actionable diagnostics rather than an exception.");
        });
        test("Batch repeated-column mappings validate before applying and preserve original data", () =>
        {
            var source = Csv.Import("id,temperature_jan,temperature_feb,note\nA,10,11,plain", "wide.csv");
            var rows = RepeatedColumnMapping.FromSource(source);
            rows[0].Variable = rows[1].Variable = "temperature"; rows[0].Grouping = "January"; rows[1].Grouping = "February"; rows[1].Keep = false;
            Require(RepeatedColumnMapping.Validate(source, rows).Count == 0, "A complete mapping with distinct grouping values should be valid.");
            RepeatedColumnMapping.Apply(source, rows);
            Require(source.Fields[1].Measure == "temperature" && source.Fields[2].Occasion == "February" && !source.Fields[2].Keep, "Apply should commit the chosen metadata and keep selection together.");
            Require(source.Rows[0][1] == "10" && source.OriginalRows![0][1] == "10" && source.Fields[1].Original == "temperature_jan", "Mapping must leave source values and original names unchanged.");
            var wide = Engine.Build(new() { Sources = [source] }, Layout.Person);
            Require(wide.Fields.Any(f => f.Name == "temperature__January") && !wide.Fields.Any(f => f.Name == "temperature__February"), "Mapping selection must control the actual output schema.");
            rows[1].Grouping = "January"; Require(RepeatedColumnMapping.Validate(source, rows).Any(e => e.Contains("more than one")), "Duplicate shared-variable/grouping pairs must be refused, including currently hidden columns.");
            rows[1].Grouping = ""; Require(RepeatedColumnMapping.Validate(source, rows).Any(e => e.Contains("both")), "Incomplete mapping pairs must be refused.");
        });
        test("Repeated-column suffix inference remains an explicit editable action", () =>
        {
            Require(RepeatedColumnMapping.InferGrouping("score_month_3") == "month_3", "A grouping suffix may include its unit prefix.");
            Require(RepeatedColumnMapping.InferGrouping("question_2") == "2", "Explicit batch mapping may use numbered items without changing automatic inference.");
            var original = Csv.Import("id,question_1,question_2\nA,yes,no", "items.csv");
            Require(original.Fields.All(f => f.Measure.Length == 0), "Automatic inference should continue to leave numbered questionnaire items alone.");
        });
    }
}
