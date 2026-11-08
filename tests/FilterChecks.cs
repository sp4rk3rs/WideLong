using WideLong;

public static class FilterChecks
{
    public static void Run(Action<string, Action> test, Action<bool> assert)
    {
        View Fixture()
        {
            var source = Csv.Import("id,age,status,date,consent\n1,17,Complete,2025-01-31,true\n2,18,Complete,2025-02-01,false\n3,25,Pending,2025-03-01,true\n4,,Complete,,\n", "filter-fictional.csv");
            source.Fields[1].Kind = ValueKind.Number; source.Fields[3].Kind = ValueKind.Date; source.Fields[4].Kind = ValueKind.Boolean;
            return Engine.SourceView(source);
        }
        RowFilterRule Rule(View view, int column, FilterOperator op, string value = "", string upper = "") => new(view.Fields[column].Key, view.Fields[column].Name, view.Fields[column].Kind, op, value, upper);
        string Ids(View view) => string.Join(",", view.Rows.Select(row => row[0].Value));
        bool Rejected(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        test("Typed filters combine numeric and exact text rules with AND / OR", () =>
        {
            var view = Fixture(); var spec = new RowFilterSpec { Rules = [Rule(view, 1, FilterOperator.GreaterEqual, "18"), Rule(view, 2, FilterOperator.Equal, "Complete")] };
            assert(Ids(Filtering.Apply(view, spec)) == "2"); spec.MatchAll = false; assert(Ids(Filtering.Apply(view, spec)) == "1,2,3,4");
            assert(view.Rows.Count == 4); assert(Ids(Filtering.Apply(view, new() { Rules = [Rule(view,2,FilterOperator.Contains,"complete")] })) == "");
        });
        test("Date ranges are inclusive and Boolean rules normalize user literals", () =>
        {
            var view = Fixture(); assert(Ids(Filtering.Apply(view, new() { Rules = [Rule(view,3,FilterOperator.Between,"2025-02-01","2025-03-01")] })) == "2,3");
            assert(Ids(Filtering.Apply(view, new() { Rules = [Rule(view,4,FilterOperator.Equal,"yes")] })) == "1,3");
            assert(Ids(Filtering.Apply(view, new() { Rules = [Rule(view,4,FilterOperator.NotEqual,"true")] })) == "2");
        });
        test("Missing rules are explicit and ordinary inequalities exclude missing values", () =>
        {
            var view = Fixture(); assert(Ids(Filtering.Apply(view,new() { Rules=[Rule(view,1,FilterOperator.IsMissing)] })) == "4");
            assert(Ids(Filtering.Apply(view,new() { Rules=[Rule(view,1,FilterOperator.NotEqual,"18")] })) == "1,3");
            assert(Ids(Filtering.Apply(view,new() { Rules=[Rule(view,3,FilterOperator.IsNotMissing)] })) == "1,2,3");
        });
        test("Text rules distinguish genuine empty text from missing cells", () =>
        {
            var view = new View { Fields = [new("id",ValueKind.Text,"fictional","id"),new("text",ValueKind.Text,"fictional","text")], Rows = [ [new("1"),new("",MissingFlag:false)], [new("2"),new("",MissingFlag:true)], [new("3"),new("NA",MissingFlag:false)] ] };
            var empty = new RowFilterSpec { Rules = [Rule(view,1,FilterOperator.Equal,"")] }; assert(Ids(Filtering.Apply(view,empty)) == "1");
            assert(Ids(Filtering.Apply(view,new() { Rules=[Rule(view,1,FilterOperator.NotContains,"NA")] })) == "1");
            assert(Rejected(() => Filtering.EnsureSasCompatible(view,empty)));
        });
        test("Filter rules follow stable column keys and reject absent or retyped variables", () =>
        {
            var view = Fixture(); var spec = new RowFilterSpec { Rules = [Rule(view,1,FilterOperator.Greater,"20")] };
            var order = new[] { 2,0,4,3,1 }; var reordered = new View { Fields = order.Select(i=>view.Fields[i] with { Name="renamed_"+i }).ToList(), Rows = view.Rows.Select(row=>order.Select(i=>row[i]).ToArray()).ToList() };
            assert(Filtering.Apply(reordered,spec).Rows.Single()[1].Value == "3");
            assert(Rejected(()=>Filtering.Apply(new() { Fields=[view.Fields[0]],Rows=[] },spec)));
            var changed = new View { Fields=view.Fields.Select((f,i)=>i==1?f with { Kind=ValueKind.Text }:f).ToList(),Rows=view.Rows }; assert(Rejected(()=>Filtering.Apply(changed,spec)));
        });
        test("Malformed filter values and reversed ranges are blocked before filtering", () =>
        {
            var view=Fixture(); assert(Rejected(()=>Filtering.Apply(view,new() { Rules=[Rule(view,1,FilterOperator.Greater,"not a number")] })));
            assert(Rejected(()=>Filtering.Apply(view,new() { Rules=[Rule(view,3,FilterOperator.Between,"2025-03-01","2025-02-01")] })));
            assert(Rejected(()=>Filtering.Apply(view,new() { Rules=[Rule(view,2,FilterOperator.Greater,"A")] })));
        });
        test("Filter copies are isolated and code predicates retain typed and missing semantics", () =>
        {
            var view=Fixture(); var spec=new RowFilterSpec { Rules=[Rule(view,1,FilterOperator.Between,"18","25"),Rule(view,3,FilterOperator.IsMissing)] };var copy=spec.Copy();copy.Rules.Clear();copy.MatchAll=false;assert(spec.Rules.Count==2&&spec.MatchAll);
            string r=Filtering.RExpression(view.Fields,spec), sas=Filtering.SasExpression(view.Fields,spec);
            assert(r.Contains("z <- prepared[[2]]") && r.Contains("!is.finite(n)") && r.Contains("is.na(prepared[[4]])"));
            assert(sas.Contains("input(_dh_o2, ?? best32.) >= 18") && sas.Contains("missing(_dh_o4)"));
            assert(Rejected(()=>Filtering.SasExpression(view.Fields,new() { Rules=[Rule(view,2,FilterOperator.Equal,"Complete ")] })));
        });
    }
}
