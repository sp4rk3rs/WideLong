using WideLong;

public static class MissingRulesChecks
{
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }

    public static Session Fixture()
    {
        var source = Csv.Import("id,score,amount,label,empty_text\n99,99,99,NA,\n100,2,100,NULL,\n101,,99,literal,", "variable_rules.csv");
        source.MissingTokens = ["", "NULL"];
        source.Fields[1].MissingTokens = ["", "99"];
        source.Fields[3].MissingTokens = [];
        source.Fields[4].MissingTokens = [];
        foreach (int c in new[] { 1, 2 })
        {
            var tokens = source.EffectiveMissingTokens(c).ToArray();
            source.Steps.Add(new(StepKind.Convert, c, Target: ValueKind.Number, MissingTokens: tokens));
            foreach (var row in source.Rows) row[c] = Values.ConvertWithMissing(row[c], ValueKind.Number, "yyyy-MM-dd", tokens);
            source.Fields[c].Kind = ValueKind.Number;
        }
        return new() { Sources = [source] };
    }

    public static void Run(Action<string, Action> test)
    {
        test("Variable missing codes stay scoped and genuine empty Text survives", () =>
        {
            var session = Fixture(); var source = session.Sources[0]; var view = Engine.SourceView(source);
            Equal(false, view.Rows[0][0].Missing); Equal("99", view.Rows[0][0].Value);
            Equal(true, view.Rows[0][1].Missing); Equal(false, view.Rows[0][2].Missing); Equal("99", view.Rows[0][2].Value);
            Equal(false, view.Rows[0][3].Missing); Equal("NA", view.Rows[0][3].Value);
            Equal(false, view.Rows[1][3].Missing); Equal("NULL", view.Rows[1][3].Value);
            Equal(false, view.Rows[0][4].Missing); Equal("", view.Rows[0][4].Value);
            Equal(3, Engine.Build(session, Layout.PersonTime).Rows.Count);
            Equal("99", source.OriginalRows![0][1]);
        });
        test("Variable rules inherit/reset and session copies isolate token sets", () =>
        {
            var source = Csv.Import("id,score,amount\n99,99,99", "scoped.csv"); source.MissingTokens = ["99"];
            source.Fields[1].MissingTokens = []; Equal(false, source.IsMissing("99", 1)); Equal(true, source.IsMissing("99", 2));
            var copy = source.Copy(); copy.Fields[1].MissingTokens!.Add("99"); copy.MissingTokens.Clear();
            Equal(false, source.IsMissing("99", 1)); Equal(true, source.IsMissing("99", 2));
            source.Fields[1].MissingTokens = null; Equal(true, source.IsMissing("99", 1));
        });
        test("Conversion review counts invalid values, newly missing cells and leading-zero loss", () =>
        {
            var source = Csv.Import("id,amount\n1,0012\n2,bad\n3,\n4,99", "preview.csv"); source.MissingTokens = []; source.Fields[1].MissingTokens = ["99"];
            var preview = Values.PreviewConversion(source, [1], ValueKind.Number, "yyyy-MM-dd");
            Equal(4, preview.Total); Equal(1, preview.Invalid); Equal(1, preview.LeadingZeroLoss); Equal(2, preview.Missing); Equal(1, preview.NewlyMissing);
            Equal(true, preview.Examples.Any(x => x.Before == "0012" && x.After == "12"));
            Equal(true, preview.Examples.Any(x => x.Before == "bad" && x.Invalid)); Equal("0012", source.Rows[0][1]);
        });
        test("Removing a numeric missing-code rule exposes validation instead of coercion", () =>
        {
            var source = Csv.Import("id,amount\n1,MISS\n2,2", "policy.csv"); source.Fields[1].Kind = ValueKind.Number; source.Fields[1].MissingTokens = ["MISS"];
            Equal(0, Values.ValidatePolicies(source).Count); source.Fields[1].MissingTokens = [];
            Equal(1, Values.ValidatePolicies(source).Count); Equal("MISS", source.Rows[0][1]);
        });
        test("Generated code carries each variable policy and recorded conversion policy", () =>
        {
            var session = Fixture(); var source = session.Sources[0];
            var plan = Recipe.Plan(session, Layout.PersonTime, MatchMode.AllPeople, false, null, new(), "");
            string r = Recipe.R(plan).Replace("\n    ", " "), sas = SasRecipe.Generate(plan).Replace("\n    ", " ");
            Equal(true, r.Contains("replace(variable_rules$score, variable_rules$score %in% \"99\", NA)")); Equal(false, r.Contains("variable_rules$amount %in% \"99\""));
            Equal(false, r.Contains("\"NULL\"")); Equal(false, r.Contains("variable_rules$id %in%"));
            Equal(false, sas.Contains("if id in")); Equal(false, sas.Contains("if amount in ('99')"));
            Equal(true, sas.Contains("if score in ('99') then score = '';"));
            Equal(false, sas.Contains("'NULL'"));
            Equal(true, source.Rows.Select((row, i) => row.SequenceEqual(Recipe.Replay(source)[i])).All(x => x));
        });
    }
}
