using WideLong;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

// Optional pressure test over a folder of real CSV files: <output> --stress <data folder> [<stress output folder>]. Nothing from that folder is kept in this tree.
if (args.Length >= 3 && args[1] == "--stress") Environment.Exit(StressChecks.Run(args[2], args.Length > 3 ? args[3] : Path.Combine(args[2], "stress-output"), Environment.GetEnvironmentVariable("WIDELONG_RSCRIPT")));
int passed = 0;
void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { Console.Error.WriteLine("FAIL " + name + ": " + ex.ToString().Replace("\r", "").Replace("\n", " | ")); Environment.Exit(1); } }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
void Throws(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Expected validation failure"); }
HashSet<string> DemoRows(Session session) { var keys=Engine.SuggestedRowVariables(session); keys.Add(session.Sources[1].Key+":3"); return keys; }
Session With(params Source[] sources) => new() { Sources = sources.ToList() };

Test("CSV quoted delimiters, multiline, quotes, BOM and trailing empty fields", () =>
{
    var records = Csv.Parse("\ufeffid,note,empty\r\n001,\"a,b\n\"\"quoted\"\"\",\r\n");
    Equal(2, records.Count); Equal("001", records[1][0]); Equal("a,b\n\"quoted\"", records[1][1]); Equal("", records[1][2]);
});
Test("CSV round trip preserves literal NA, empty, Unicode, newlines and spaces", () =>
{
    var s = Csv.Import("id,value\n001,NA\n002,\"\"\n003,\" α\nβ \"\n", "round.csv");
    using var writer = new StringWriter(); Csv.Write(writer, Engine.SourceView(s));
    var round = Csv.Import(writer.ToString(), "round.csv");
    for (int i = 0; i < s.Rows.Count; i++) Equal(s.Rows[i][1], round.Rows[i][1]);
});
Test("Malformed CSV is rejected rather than silently repaired", () =>
{
    Throws(() => Csv.Parse("a,b\n1,2,3")); Throws(() => Csv.Parse("a,b\n1,\"unterminated")); Throws(() => Csv.Import("id,id\n1,2", "bad.csv")); Throws(() => Csv.Parse("a\n\"x\"oops"));
});
Test("Semicolon and tab imports", () => { Equal("001", Csv.Import("id;x\n001;y", "semi.csv", ';').Rows[0][0]); Equal(2, Csv.Parse("a\tb\n1\t2", '\t')[0].Length); });
Test("Single-column empty records survive CSV export", () =>
{
    var s = Csv.Import("value\n\"\"\n\"\"\n", "blank.csv"); using var writer = new StringWriter(); Csv.Write(writer, Engine.SourceView(s)); Equal(2, Csv.Import(writer.ToString(), "blank.csv").Rows.Count);
});
Test("Conservative ID inference and explicit time override", () =>
{
    Equal(-1, Csv.Import("row_number,name\n1,A\n2,B", "unknown.csv").IdColumn);
    var s = Csv.Import("participant_id,visit,x\n001,m0,4\n001,m3,5", "visits.csv"); Equal(0, s.IdColumn); Equal(1, s.TimeColumn);
});
Test("Baseline one-to-many matching does not multiply visits", () =>
{
    var a = Csv.Import("id,age\n001,25\n002,30", "people.csv");
    var b = Csv.Import("id,visit,score\n001,m0,4\n001,m3,5\n003,m0,6", "visits.csv");
    var all = Engine.Build(With(a, b), Layout.PersonTime); Equal(4, all.Rows.Count); Equal(4, all.Fields.Count);
    Equal(2, Engine.Build(With(a, b), Layout.PersonTime, MatchMode.CommonPeople).Rows.Count);
    Equal(3, Engine.Build(With(a, b), Layout.PersonTime, MatchMode.FirstFilePeople).Rows.Count);
});
Test("Duplicate join keys and missing keys/time are blocked", () =>
{
    Throws(() => Engine.Build(With(Csv.Import("id,x\n1,a\n1,b", "dup.csv")), Layout.PersonTime));
    var missing = Csv.Import("id,x\n,a", "missing.csv"); missing.IdColumn = 0; Throws(() => Engine.Build(With(missing), Layout.PersonTime));
    Throws(() => Engine.Build(With(Csv.Import("id,visit,x\n1,,a", "missingtime.csv")), Layout.PersonTime));
});
Test("Wide patterns are editable and lossless across layouts", () =>
{
    var s = Csv.Import("id,group,score_m0,score_m3\n001,A,4,5\n002,B,6,7", "wide.csv");
    Equal("score", s.Fields[2].Measure);
    var longView = Engine.Build(With(s), Layout.PersonTime); Equal(4, longView.Rows.Count); Equal(4, longView.Fields.Count);
    var wide = Engine.Build(With(s), Layout.Person); Equal(2, wide.Rows.Count); Equal(4, wide.Fields.Count);
    Equal("score__m0", wide.Fields[2].Name); Equal("4", wide.Rows[0][2].Value); Equal("5", wide.Rows[0][3].Value);
    var measurement = Engine.Build(With(s), Layout.Measurement, rowVariables: Engine.SuggestedRowVariables(With(s))); Equal(4, measurement.Rows.Count); Equal("group", measurement.Fields[2].Name); Equal("A", measurement.Rows[0][2].Value);
    Equal("4", s.Rows[0][2]); Equal("score_m0", s.Fields[2].Original);
});
Test("Repeated mappings cannot collapse duplicate values or mix types", () =>
{
    var s = Csv.Import("id,x_m0,x_m3\n1,4,5", "wide.csv"); s.Fields[2].Occasion = "m0";
    Throws(() => Engine.Build(With(s), Layout.PersonTime)); s.Fields[2].Occasion = "m3"; s.Fields[2].Kind = ValueKind.Number;
    Throws(() => Engine.Build(With(s), Layout.PersonTime));
});
Test("Time labels and IDs match exactly", () =>
{
    var a = Csv.Import("id,visit,a\n001,baseline,1", "a.csv"); var b = Csv.Import("id,visit,b\n001,m0,2\n1,m0,3", "b.csv");
    Equal(3, Engine.Build(With(a, b), Layout.PersonTime).Rows.Count);
});
Test("Unselected variables are omitted and key roles survive selection", () =>
{
    var s = Csv.Import("id,x,y\n001,1,2", "a.csv"); s.Fields[0].Keep = false; s.Fields[2].Keep = false;
    Equal(3, Engine.Build(With(s), Layout.PersonTime).Fields.Count); Equal(2, Engine.SourceView(s).Fields.Count);
});
Test("Name collisions are qualified and source groups remain adjacent", () =>
{
    var view = Engine.Build(With(Csv.Import("id,x,y\n1,a,b", "a.csv"), Csv.Import("id,x,z\n1,c,d", "b.csv")), Layout.PersonTime);
    Equal("a.x", view.Fields[2].Name); Equal("y", view.Fields[3].Name); Equal("b.x", view.Fields[4].Name);
});
Test("Numeric conversion preserves text IDs and refuses invalid values", () =>
{
    Equal(ValueKind.Text, Inference.SuggestedKind(new[] { "001", "002" })); Equal("1.2", Values.Convert("1.20", ValueKind.Number)); Equal("", Values.Convert("", ValueKind.Number));
    Throws(() => Values.Convert("NA", ValueKind.Number)); Throws(() => Values.Convert("1,2", ValueKind.Number));
    Equal(1, Values.Failures(Csv.Import("id,x\n1,2\n2,bad", "a.csv"), 1, ValueKind.Number, "yyyy-MM-dd").Count);
});
Test("Dates use an explicit format and Booleans are normalized", () =>
{
    Equal("2025-03-04", Values.Convert("03/04/2025", ValueKind.Date, "MM/dd/yyyy")); Throws(() => Values.Convert("03/04/2025", ValueKind.Date)); Equal("true", Values.Convert("yes", ValueKind.Boolean)); Equal("false", Values.Convert("0", ValueKind.Boolean));
});
Test("Edits map back to source cells in every layout", () =>
{
    var s = Csv.Import("id,x_m0,x_m3\n001,4,5", "a.csv");
    var v = Engine.Build(With(s), Layout.PersonTime); var origin = v.Rows[0][2].Origin!; Equal(1, origin.Column);
    s.Rows[origin.Row][origin.Column] = "99";
    Equal("99", Engine.Build(With(s), Layout.Person).Rows[0][1].Value);
    Equal("99", Engine.Build(With(s), Layout.Measurement, rowVariables: Engine.SuggestedRowVariables(With(s))).Rows[0][3].Value);
});
Test("Stacking aligns names, preserves overlapping IDs and records provenance", () =>
{
    var a = Csv.Import("id,x,y\n1,a,b", "a.csv"); var b = Csv.Import("id,y,x\n1,d,c", "b.csv");
    var v = Engine.Build(With(a, b), Layout.PersonTime, stack: true); Equal(2, v.Rows.Count); Equal("a.csv", v.Rows[0][2].Value); Equal("c", v.Rows[1][3].Value);
    Throws(() => Engine.Build(With(a, b), Layout.Person, stack: true));
});
Test("Session snapshots do not share mutable arrays or fields", () =>
{
    var session = With(Csv.Import("id,x\n1,a", "a.csv")); var copy = session.Copy(); copy.Sources[0].Rows[0][1] = "b"; copy.Sources[0].Fields[1].Name = "renamed";
    Equal("a", session.Sources[0].Rows[0][1]); Equal("x", session.Sources[0].Fields[1].Name);
});
Test("Stacked provenance never overwrites a user variable", () =>
{
    var s = Csv.Import("id,__source_file,source_file\n1,user value,another value", "reserved.csv");
    var v = Engine.Build(With(s), Layout.PersonTime, stack: true); Equal(5, v.Fields.Count); Equal("user value", v.Rows[0][3].Value); Equal("another value", v.Rows[0][4].Value);
});
Test("600 people / 43-column wide fixture joins and pivots without loss", () =>
{
    var watch = Stopwatch.StartNew(); var session = Demo.Create();
    Equal(600, session.Sources[2].Rows.Count); Equal(43, session.Sources[2].Fields.Count);
    var longView = Engine.Build(session, Layout.PersonTime); Equal(1800, longView.Rows.Count); Equal(24, longView.Fields.Count);
    var wide = Engine.Build(session, Layout.Person); Equal(600, wide.Rows.Count); Equal(57, wide.Fields.Count);
    var measurements = Engine.Build(session, Layout.Measurement, rowVariables: DemoRows(session)); Equal(27000, measurements.Rows.Count); Equal(12, measurements.Fields.Count);
    Console.WriteLine($"  Synthetic benchmark: all three layouts in {watch.ElapsedMilliseconds} ms");
});
Test("SAS-compatible headers are unique and bounded", () =>
{
    var v = new View { Fields = new[] { "123 weird name", new string('a', 60), new string('a', 60), "_dh_raw_0" }.Select(n => new OutputField(n, ValueKind.Text, "test")).ToList() };
    var names = Export.SasNames(v); Equal(4, names.Distinct(StringComparer.OrdinalIgnoreCase).Count()); Equal(true, names.All(n => n.Length <= 32));
});
Test("Column clicks add sort priorities and click-again removes them", () =>
{
    var p = new ViewPreferences(); p.ToggleSort("a"); p.ToggleSort("b"); Equal("a,b", string.Join(",", p.Sorts.Select(s => s.Key))); p.ToggleSort("a"); Equal("b", p.Sorts.Single().Key);
});
Test("Typed multi-column sorting is stable and blanks stay last", () =>
{
    var s = Csv.Import("id,amount,group\na,10,B\nb,2,A\nc,2,A\nd,,A\ne,3,B", "sorting.csv"); s.Fields[1].Kind = ValueKind.Number;
    var raw = Engine.SourceView(s); var p = new ViewPreferences(); p.ToggleSort(raw.Fields[2].Key); p.ToggleSort(raw.Fields[1].Key);
    var sorted = Presentation.Apply(raw, p); Equal("b,c,d,e,a", string.Join(",", sorted.Rows.Select(r => r[0].Value))); Equal("a", s.Rows[0][0]);
    p.Sorts = [new(raw.Fields[1].Key, true)]; Equal("a,e,b,c,d", string.Join(",", Presentation.Apply(raw, p).Rows.Select(r => r[0].Value)));
});
Test("Dragging any column reorders export and dragging sorted columns updates priority", () =>
{
    var raw = Engine.SourceView(Csv.Import("id,x,y\n001,A,B", "order.csv")); var p = new ViewPreferences();
    p.ToggleSort(raw.Fields[1].Key); p.ToggleSort(raw.Fields[2].Key);
    p.SetColumnOrder([raw.Fields[2].Key, raw.Fields[0].Key, raw.Fields[1].Key], true);
    var reordered = Presentation.Apply(raw, p); Equal("y,id,x", string.Join(",", reordered.Fields.Select(f => f.Name))); Equal("B,001,A", string.Join(",", reordered.Rows[0].Select(c => c.Value))); Equal(raw.Fields[2].Key, p.Sorts[0].Key);
    using var writer = new StringWriter(); Csv.Write(writer, reordered); Equal("y,id,x", Csv.Parse(writer.ToString())[0].Aggregate((a,b) => a + "," + b));
});
Test("Edit provenance remains correct after sorting and reordering", () =>
{
    var s = Csv.Import("id,age,name\n001,42,Avery\n002,25,Jordan", "edits.csv"); s.Fields[1].Kind = ValueKind.Number; var raw = Engine.SourceView(s); var p = new ViewPreferences();
    p.ToggleSort(raw.Fields[1].Key); p.SetColumnOrder([raw.Fields[2].Key,raw.Fields[1].Key,raw.Fields[0].Key], false);
    var result = Presentation.Apply(raw, p); var origin = result.Rows[0][0].Origin!; Equal(1, origin.Row); Equal(2, origin.Column); Equal("Jordan", s.Rows[origin.Row][origin.Column]);
});
Test("Dragging an unsorted column leaves the user's chosen sort priority intact", () =>
{
    var p = new ViewPreferences(); p.Reconcile(["id","age","name"]); p.ToggleSort("name"); p.ToggleSort("age");
    p.ReorderColumns(["age","id","name"], "id"); Equal("name,age", string.Join(",", p.Sorts.Select(s=>s.Key)));
    p.ReorderColumns(["name","age","id"], "name"); Equal("name,age", string.Join(",", p.Sorts.Select(s=>s.Key)));
});
Test("Hidden columns retain placement and sorting chips change priority", () =>
{
    var p = new ViewPreferences(); p.Reconcile(["a","b","c","d"]); p.SetColumnOrder(["d","a","c"], false); Equal("d,b,a,c", string.Join(",", p.ColumnOrder));
    p.ToggleSort("a"); p.ToggleSort("b"); p.ToggleSort("c"); p.MoveSort("c","a"); Equal("c,a,b", string.Join(",", p.Sorts.Select(s => s.Key)));
});
Test("Filtering, renaming, and sorting do not alter the imported working values", () =>
{
    var s = Csv.Import("id,name\n001,Avery\n002,Jordan", "names.csv"); var raw = Engine.SourceView(s); var p = new ViewPreferences(); p.Names[raw.Fields[1].Key] = "participant_name"; p.ToggleSort(raw.Fields[1].Key);
    var result = Presentation.Apply(raw, p, "avery"); Equal(1, result.Rows.Count); Equal("participant_name", result.Fields[1].Name); Equal("name", s.Fields[1].Name); Equal(2, s.Rows.Count);
});
Test("Fictional demo uses recognizable names, visits, and plausible typed measures", () =>
{
    var s = Demo.Create(); Equal("Avery Reed", s.Sources[0].Rows[0][1]); Equal("Baseline", s.Sources[1].Rows[0][1]); Equal("systolic_bp", s.Sources[2].Fields[1].Measure); Equal(ValueKind.Number, s.Sources[2].Fields[1].Kind);
});
Test("Explicit export bundle contains data, types and offline import scripts", () =>
{
    if (args.Length != 1) throw new Exception("Pass a scratch directory within work/ for explicit synthetic export tests.");
    Directory.CreateDirectory(args[0]);
    var source = Csv.Import("id,score,date,flag\n001,2.5,2025-01-01,true\n002,,,", "example.csv");
    source.Fields[1].Kind = ValueKind.Number; source.Fields[2].Kind = ValueKind.Date; source.Fields[3].Kind = ValueKind.Boolean;
    var view = Engine.SourceView(source); string destination = Path.Combine(args[0], "synthetic-export.zip"); Export.SaveBundle(destination, view);
    using var archive = ZipFile.OpenRead(destination); Equal(5, archive.Entries.Count);
    using var r = new StreamReader(archive.GetEntry("import.R")!.Open()); var script = r.ReadToEnd(); Equal(true, script.Contains("as.numeric(data[[2]])")); Equal(false, script.Contains("001"));
    using var data = new StreamReader(archive.GetEntry("data.csv")!.Open()); Equal("001", Csv.Parse(data.ReadToEnd())[1][0]);
    var multi = Engine.SourceView(Csv.Import("id,x\n1,\"hello\nworld\"", "multiline.csv")); Throws(() => Export.SaveBundle(Path.Combine(args[0], "must-not-exist.zip"), multi)); Equal(false, File.Exists(Path.Combine(args[0], "must-not-exist.zip")));
});
Test("Recorded operations replay from immutable original rows, including undo snapshots", () =>
{
    var s = Csv.Import("id,age\n001,not given\n002,25", "history.csv");
    s.Steps.Add(new(StepKind.Replace,1,Before:"not given",After:"")); s.Rows[0][1]="";
    s.Steps.Add(new(StepKind.Convert,1,Target:ValueKind.Number)); s.Fields[1].Kind=ValueKind.Number;
    s.Steps.Add(new(StepKind.Cell,1,1,"25","30")); s.Rows[1][1]="30";
    Equal("not given",s.OriginalRows![0][1]); Equal("30",Recipe.Replay(s)[1][1]);
    var copy=s.Copy(); Equal(true,ReferenceEquals(s.OriginalRows,copy.OriginalRows)); copy.Steps.Clear(); Equal(3,s.Steps.Count);
    var plan=Recipe.Plan(With(s),Layout.PersonTime,MatchMode.AllPeople,false,null,new(),"");
    string sas=SasRecipe.Generate(plan), r=Recipe.R(plan);
    Equal(true,sas.Contains("data history;")); Equal(true,sas.Contains("data prepared_data;")); Equal(true,sas.Contains("not given")); Equal(true,r.Contains("history[2, \"age\"] <- 30  # was \"25\"")); Equal(true,sas.Contains("if _n_ = 2 then age_n = 30;"));
});
Test("Unrecorded changes are rejected instead of producing incomplete cleaning code", () =>
{
    var s=Csv.Import("id,x\n1,a", "untracked.csv"); s.Rows[0][1]="b";
    Throws(()=>Recipe.Plan(With(s),Layout.PersonTime,MatchMode.AllPeople,false,null,new(),""));
});
Test("All six examples generate SAS and R cleaning code without embedding datasets", () =>
{
    Equal(6,Examples.All.Count);
    foreach(var example in Examples.All)
    {
        var s=example.Create(); var plan=Recipe.Plan(s,Layout.PersonTime,MatchMode.AllPeople,example.Stack,null,new(),"");
        var sas=SasRecipe.Generate(plan); var r=Recipe.R(plan);
        Equal(true,sas.Contains("infile \"&folder")); Equal(true,sas.Contains("data prepared_data;")); Equal(true,r.Contains("read_text <- function")); Equal(true,r.Contains("rownames(prepared) <- NULL"));
        if(example==Examples.All[0]) { Equal(false,r.Contains("Avery Reed")); Equal(false,sas.Contains("Avery Reed")); }
    }
});
Test("SAS blocks unsupported whitespace/multiline cases and R remains available", () =>
{
    var s=Csv.Import("id,note\n001,\"leading and trailing \"", "spaces.csv");
    var p=Recipe.Plan(With(s),Layout.Source,MatchMode.AllPeople,false,null,new(),""); Throws(()=>SasRecipe.Generate(p)); Equal(true,Recipe.R(p).Contains("prepared <- spaces"));
});
Test("Code export refuses numeric replay that could silently change precision", () =>
{
    var s=Csv.Import("id,value\n1,0.00000001", "precision.csv");
    s.Steps.Add(new(StepKind.Convert,1,Target:ValueKind.Number));s.Rows[0][1]=Values.Convert(s.Rows[0][1],ValueKind.Number);s.Fields[1].Kind=ValueKind.Number;
    Throws(()=>Recipe.Plan(With(s),Layout.PersonTime,MatchMode.AllPeople,false,null,new(),""));
});
Test("Example code ZIP contains unchanged fictional sources and complete scripts", () =>
{
    var s=Examples.All[4].Create();var source=s.Sources[0];source.Steps.Add(new(StepKind.Replace,1,Before:"not given",After:""));foreach(var row in source.Rows)if(row[1]=="not given")row[1]="";
    var p=Recipe.Plan(s,Layout.PersonTime,MatchMode.AllPeople,false,null,new(),"");
    string path=Path.Combine(args[0],"example-reproducible.zip");Recipe.SaveExampleBundle(path,p);
    using var archive=ZipFile.OpenRead(path);Equal(3+s.Sources.Count,archive.Entries.Count);Equal(true,archive.GetEntry("clean.sas")!=null);Equal(true,archive.GetEntry("clean.R")!=null);Equal(true,archive.GetEntry("data.csv")==null);
    using var reader=new StreamReader(archive.GetEntry(source.Name)!.Open());Equal(true,reader.ReadToEnd().Contains("not given"));
});
Test("Variables-as-rows keeps names and visit details as columns", () =>
{
    var s=Demo.Create(2);var view=Engine.Build(s,Layout.Measurement,rowVariables:DemoRows(s));
    int variable=view.Fields.FindIndex(f=>f.Key=="@variable");int value=view.Fields.FindIndex(f=>f.Key=="@value");int name=view.Fields.FindIndex(f=>f.Name=="name");int date=view.Fields.FindIndex(f=>f.Name=="visit_date");
    Equal(true,name>=0&&date>=0);Equal(ValueKind.Number,view.Fields[value].Kind);Equal("Avery Reed",view.Rows[0][name].Value);Equal(true,view.Rows.All(r=>r[variable].Value!="name"&&r[variable].Value!="age"&&r[variable].Value!="visit_date"&&r[variable].Value!="appointment_status"));
    var origin=view.Rows[0][value].Origin!;Equal(s.Sources[1].Key,origin.SourceKey);Equal("61",s.Sources[1].Rows[origin.Row][origin.Column]);
});
Test("Explicit row-variable choices control what stacks and retain other numeric columns", () =>
{
    var s=Demo.Create(2);string weight=s.Sources[1].Key+":3";var view=Engine.Build(s,Layout.Measurement,rowVariables:[weight]);
    Equal(6,view.Rows.Count);Equal(true,view.Fields.Any(f=>f.Name=="systolic_bp"));int v=view.Fields.FindIndex(f=>f.Key=="@variable");Equal(true,view.Rows.All(r=>r[v].Value=="weight_kg"));
    var prefs=new ViewPreferences {RowVariables=[weight]};var copy=prefs.Copy();copy.RowVariables!.Clear();Equal(1,prefs.RowVariables.Count);
});
Test("Flat files need an explicit choice instead of silently treating demographics as measurements", () =>
{
    var s=Csv.Import("id,name,age\n001,Avery,25\n002,Jordan,30", "flat.csv");s.Fields[2].Kind=ValueKind.Number;
    Throws(()=>Engine.Build(With(s),Layout.Measurement));
    var chosen=Engine.Build(With(s),Layout.Measurement,rowVariables:[s.Key+":1"]);Equal(2,chosen.Rows.Count);Equal(true,chosen.Fields.Any(f=>f.Name=="age"));
});

Test("Wide selection omits deselected occasions but preserves selected missing columns",()=> {
    var a=Csv.Import("id,temperature_m0,temperature_m3,temperature_m6\n1,20,21,", "wide.csv");
    var b=Csv.Import("id,visit,status\n1,m0,ok\n1,m3,ok\n1,m6,ok", "long.csv");
    a.Fields[2].Keep=false; var v=Engine.Build(With(a,b),Layout.Person);
    Equal(false,v.Fields.Any(f=>f.Name=="temperature__m3"));
    int c=v.Fields.FindIndex(f=>f.Name=="temperature__m6"); Equal(true,c>=0); Equal(true,v.Rows[0][c].Missing);
});
Test("Explicit missing policies distinguish missing codes, literal NA and empty text",()=> {
    var s=Csv.Import("id,text,num\n1,NA,NA\n2,,2\n3,NULL,", "policy.csv");
    s.MissingTokens=["NULL"]; var v=Engine.SourceView(s);
    Equal(false,v.Rows[0][1].Missing);Equal(false,v.Rows[1][1].Missing);Equal(true,v.Rows[2][1].Missing);
    s.MissingTokens.Add("NA"); Equal(0,Values.Failures(s,2,ValueKind.Number,"yyyy-MM-dd").Count);
    s.Fields[2].Kind=ValueKind.Number; Equal(true,Engine.SourceView(s).Rows[2][2].Missing);
    var copy=s.Copy();copy.MissingTokens.Clear();Equal(2,s.MissingTokens.Count);
});
Test("ID and grouping recommendations use shared IDs and preserve manual choices",()=> {
    var a=Csv.Import("customer_id,name\nC1,A\nC2,B", "customers.csv");
    var b=Csv.Import("order_id,customer_id,order_number,total\nO1,C1,First,10\nO2,C1,Second,20\nO3,C2,First,30", "orders.csv");
    var s=With(a,b);KeyRecommendations.Recommend(s);Equal(1,b.IdColumn);Equal(2,b.TimeColumn);Equal("customer_id",SchemaNaming.IdName(s));Equal("order_number",SchemaNaming.GroupName(s));
    b.IdChosen=true;b.GroupChosen=true;b.IdColumn=0;b.TimeColumn=-1;KeyRecommendations.Recommend(s);Equal(0,b.IdColumn);Equal(-1,b.TimeColumn);
    var items=Csv.Import("id,question_1,question_2\n1,yes,no", "items.csv");Equal(true,items.Fields.All(f=>f.Measure.Length==0));
});
Test("Software export conventions and both-layout ZIP preserve separate schemas",()=> {
    var s=Csv.Import("id,text,num\n1,NA,\n2,,2", "missing.csv");s.Fields[2].Kind=ValueKind.Number;var v=Engine.SourceView(s);
    string folder=args[0];Export.SaveCsv(Path.Combine(folder,"sas-missing.csv"),v,ExportTarget.Sas);
    Equal(".",Csv.Parse(File.ReadAllText(Path.Combine(folder,"sas-missing.csv")))[1][2]);
    Throws(()=>Export.SaveCsv(Path.Combine(folder,"r-collision.csv"),v,ExportTarget.R));
    Export.SaveBundle(Path.Combine(folder,"r-literal-na.zip"),v,ExportTarget.R);
    using(var zip=ZipFile.OpenRead(Path.Combine(folder,"r-literal-na.zip"))) {using var reader=new StreamReader(zip.GetEntry("data.csv")!.Open());var rows=Csv.Parse(reader.ReadToEnd());Equal("NA",rows[1][1]);Equal(false,rows[1][2]=="NA");}
    var data=Demo.Create(2);Export.SaveBundles(Path.Combine(folder,"both.zip"),new Dictionary<string,View> { ["wide"]=Engine.Build(data,Layout.Person), ["long"]=Engine.Build(data,Layout.PersonTime) });
    using var both=ZipFile.OpenRead(Path.Combine(folder,"both.zip"));Equal(10,both.Entries.Count);Equal(true,both.GetEntry("wide/data.csv")!=null&&both.GetEntry("long/import.R")!=null);
});

ExportReviewChecks.Run(Test, condition=>Equal(true,condition));
FilterChecks.Run(Test, condition=>Equal(true,condition));
MissingRulesChecks.Run(Test);
DiagnosticsChecks.Run(Test);
CompoundFixtureChecks.Run(Test);
CompoundKeyChecks.Run(Test);
RegressionChecks.Run(Test, condition=>Equal(true,condition));
FeatureChecks.Run(Test, condition=>Equal(true,condition));
Test("Recommendations keep person and time as separate matching dimensions",()=> {
    var source=Csv.Import("id,person,time,score\nA,Patient,Baseline,1\nA,Spouse,Baseline,2\nA,Patient,Month 3,3\nA,Spouse,Month 3,4", "roles.csv");
    var data=With(source);KeyRecommendations.Recommend(data);Equal(0,source.IdColumn);Equal(1,source.EntityColumn);Equal(2,source.TimeColumn);Equal(4,Engine.Build(data,Layout.PersonTime).Rows.Count);
    var wide=Csv.Import("id,person,confidence_m0,confidence_m3\nA,Patient,1,2\nA,Spouse,3,4", "roles_wide.csv");data=With(wide);KeyRecommendations.Recommend(data);Equal(1,wide.EntityColumn);Equal(-1,wide.TimeColumn);Equal(4,Engine.Build(data,Layout.PersonTime).Rows.Count);
});
Source NumericIds(string text, string name, params int[] columns)
{
    var s=Csv.Import(text,name);
    foreach(int c in columns){s.Steps.Add(new(StepKind.Convert,c,Target:ValueKind.Number));foreach(var row in s.Rows)row[c]=Values.Convert(row[c],ValueKind.Number);s.Fields[c].Kind=ValueKind.Number;}
    return s;
}
Test("Leading-zero ID conversions preserve matching, originals and replay across five files",()=> {
    var data=Examples.All[0].Create();
    var targets=data.Sources.Select(s=>(Source:s,Column:s.IdColumn)).ToList();
    Equal(4800,targets.Sum(t=>t.Source.Rows.Count));
    Equal(0,Values.KeyConversionCollisions(targets,ValueKind.Number,"yyyy-MM-dd"));
    var before=Engine.Build(data,Layout.PersonTime);
    var beforeWide=Engine.Build(data,Layout.Person);
    foreach(var (source,column) in targets) {
        var preview=Values.PreviewConversion(source,[column],ValueKind.Number,"yyyy-MM-dd");
        Equal(0,preview.Invalid); Equal(source.Rows.Count,preview.LeadingZeroLoss);
        source.CaptureOriginal();
        source.Steps.Add(new(StepKind.Convert,column,Target:ValueKind.Number,MissingTokens:source.EffectiveMissingTokens(column).ToArray()));
        foreach(var row in source.Rows) row[column]=Values.Convert(row[column],ValueKind.Number);
        source.Fields[column].Kind=ValueKind.Number;
        Equal("00001",source.OriginalRows![0][column]); Equal("1",source.Rows[0][column]);
    }
    Equal(before.Rows.Count,Engine.Build(data,Layout.PersonTime).Rows.Count);
    Equal(beforeWide.Rows.Count,Engine.Build(data,Layout.Person).Rows.Count);
    foreach(var layout in new[]{Layout.PersonTime,Layout.Person}) {
        var view=Engine.Build(data,layout); Equal(ValueKind.Number,view.Fields[0].Kind);
        var plan=Recipe.Plan(data,layout,MatchMode.AllPeople,false,null,new ViewPreferences(),"");
        Equal(ValueKind.Number,plan.Columns.Single(c=>c.Role=="id").Field.Kind);
        Equal(true,Recipe.R(plan).Contains("as.numeric(")); Equal(true,SasRecipe.Generate(plan).Contains("input("));
    }
});
Test("Key conversion rejects distinct values collapsing within and across files",()=> {
    var same=Csv.Import("id,x\n007,a\n7,b","same.csv");
    Equal(1,Values.KeyConversionCollisions([(same,0)],ValueKind.Number,"yyyy-MM-dd"));
    var a=Csv.Import("id,x\n007,a\n007,b","a.csv");
    var b=Csv.Import("id,x\n7,c","b.csv");
    Equal(0,Values.KeyConversionCollisions([(a,0)],ValueKind.Number,"yyyy-MM-dd"));
    Equal(1,Values.KeyConversionCollisions([(a,0),(b,0)],ValueKind.Number,"yyyy-MM-dd"));
    Equal(0,Values.KeyConversionCollisions([(a,0),(b,0)],ValueKind.Text,"yyyy-MM-dd"));
    Equal(1,Values.KeyConversionCollisions([(Csv.Import("id,x\n201.0,a\n 201,b","decimal.csv"),0)],ValueKind.Number,"yyyy-MM-dd"));
});
Test("Converted ID columns produce a numeric combined ID that sorts as numbers",()=> {
    var a=NumericIds("id,visit,score\n10,1,5\n2,1,6\n1,1,7","a.csv",0); a.TimeColumn=1;
    var b=NumericIds("id,age\n10,40\n2,30\n1,20","b.csv",0);
    foreach(var layout in new[]{Layout.PersonTime,Layout.Person})
    {
        var raw=Engine.Build(With(a,b),layout); var id=raw.Fields.Single(f=>f.Key=="@person_id");
        Equal(ValueKind.Number,id.Kind);
        var p=new ViewPreferences(); p.ToggleSort("@person_id");
        Equal("1,2,10",string.Join(",",Presentation.Apply(raw,p).Rows.Select(r=>r[0].Value)));
        p.Sorts[0]=p.Sorts[0] with {Descending=true}; Equal("10,2,1",string.Join(",",Presentation.Apply(raw,p).Rows.Select(r=>r[0].Value)));
    }
    Equal(ValueKind.Text,Engine.Build(With(a,b),Layout.PersonTime).Fields.Single(f=>f.Key=="@time").Kind);
    a.Steps.Add(new(StepKind.Convert,1,Target:ValueKind.Number));a.Fields[1].Kind=ValueKind.Number;
    Equal(ValueKind.Number,Engine.Build(With(a,b),Layout.PersonTime).Fields.Single(f=>f.Key=="@time").Kind);
});
Test("Mixed ID types stay Text without adding a page notice",()=> {
    var a=NumericIds("id,score\n10,5\n2,6","a.csv",0); var b=Csv.Import("id,age\n10,40\n2,30","b.csv");
    var view=Engine.Build(With(a,b),Layout.PersonTime);
    Equal(ValueKind.Text,view.Fields[0].Kind); Equal(false,view.Notes.Any(n=>n.Contains("ID types differ",StringComparison.Ordinal))); Equal(true,Engine.KeyKindConflict(With(a,b))!=null);
    var p=new ViewPreferences(); p.ToggleSort("@person_id"); Equal("10,2",string.Join(",",Presentation.Apply(view,p).Rows.Select(r=>r[0].Value)));
    Equal(null,Engine.KeyKindConflict(With(a,NumericIds("id,age\n10,40","c.csv",0))));
});
Test("Numeric grouping stays Text when repeated-column labels are not numeric",()=> {
    var timed=NumericIds("id,wave,x\nA,1,5\nA,2,6","timed.csv",1); timed.TimeColumn=1;
    var wide=Csv.Import("id,y_baseline,y_month3\nA,1,2","wide.csv");
    Equal(ValueKind.Number,Engine.KeyKinds(With(timed)).Time);
    Equal(ValueKind.Text,Engine.KeyKinds(With(timed,wide)).Time);
});
Test("Numeric sorting keeps blanks last, valid numbers first and ties in import order",()=> {
    var s=Csv.Import("id,n\na,10\nb,\nc,2\nd,2\ne,-1.5","sortkeys.csv"); s.Fields[1].Kind=ValueKind.Number;
    var raw=Engine.SourceView(s); var p=new ViewPreferences(); p.ToggleSort(raw.Fields[1].Key);
    Equal("e,c,d,a,b",string.Join(",",Presentation.Apply(raw,p).Rows.Select(r=>r[0].Value)));
    p.Sorts[0]=p.Sorts[0] with {Descending=true}; Equal("a,c,d,e,b",string.Join(",",Presentation.Apply(raw,p).Rows.Select(r=>r[0].Value)));
    var unsorted=Presentation.Apply(raw,new ViewPreferences()); Equal(true,ReferenceEquals(raw.Rows[0],unsorted.Rows[0]));
});
Test("Sort-variable search ranks name matches, then source matches, and skips sorted variables",()=> {
    var fields=new List<OutputField>{new("participant_id",ValueKind.Text,"ID","@person_id"),new("heart_rate",ValueKind.Number,"Vitals.csv","k1"),new("rate_of_change",ValueKind.Number,"Other.csv","k2"),new("weight",ValueKind.Number,"Rates.csv","k3")};
    Equal("rate_of_change,heart_rate,weight",string.Join(",",Presentation.SortCandidates(fields,[],"rate").Select(f=>f.Name)));
    Equal("heart_rate,weight",string.Join(",",Presentation.SortCandidates(fields,["k2"],"RATE").Select(f=>f.Name)));
    Equal(4,Presentation.SortCandidates(fields,[],"  ").Count); Equal(2,Presentation.SortCandidates(fields,[],"",2).Count);
    Equal(0,Presentation.SortCandidates(fields,[],"zzz").Count);
});
Test("Preview snapshots can omit original rows without touching the session",()=> {
    var s=Demo.Create(3); s.Sources[0].Rows[0][1]="Changed"; var copy=s.Copy(includeOriginals:false);
    Equal(null,copy.Sources[0].OriginalRows); Equal(true,s.Sources[0].OriginalRows!=null); Equal("Changed",copy.Sources[0].Rows[0][1]);
    Equal(Engine.Build(s,Layout.PersonTime).Rows.Count,Engine.Build(copy,Layout.PersonTime).Rows.Count);
});
Test("Exports and code plans carry the numeric ID type",()=> {
    var a=NumericIds("id,score\n10,5\n2,6","a.csv",0); var session=With(a);
    var view=Engine.Build(session,Layout.PersonTime); string zip=Path.Combine(args[0],"numeric-id.zip"); Directory.CreateDirectory(args[0]);
    Export.SaveBundle(zip,view,ExportTarget.R);
    using(var archive=ZipFile.OpenRead(zip)){ using var reader=new StreamReader(archive.GetEntry("dictionary.csv")!.Open()); string dictionary=reader.ReadToEnd(); Equal(true,dictionary.Contains("id,id,Number,ID",StringComparison.Ordinal)); using var r2=new StreamReader(archive.GetEntry("import.R")!.Open()); Equal(true,r2.ReadToEnd().Contains("data[[1]] <- as.numeric(data[[1]])",StringComparison.Ordinal)); }
    var plan=Recipe.Plan(session,Layout.PersonTime,MatchMode.AllPeople,false,null,new ViewPreferences(),"");
    Equal(ValueKind.Number,plan.Columns.Single(c=>c.Role=="id").Field.Kind);
    Equal(true,SasRecipe.Generate(plan).Contains("input(",StringComparison.Ordinal));
});
Test("Adding sort variables moves them to the far left in priority order",()=> {
    var raw=Engine.SourceView(Csv.Import("id,a,b,c\n1,x,y,z","pin.csv")); var p=new ViewPreferences(); p.Reconcile(raw.Fields.Select(f=>f.Key));
    p.ToggleSort(raw.Fields[3].Key); p.ToggleSort(raw.Fields[1].Key); p.MoveSortsToFront();
    Equal("c,a,id,b",string.Join(",",Presentation.Apply(raw,p).Fields.Select(f=>f.Name)));
    p.MoveSort(raw.Fields[1].Key,raw.Fields[3].Key); p.MoveSortsToFront();
    Equal("a,c,id,b",string.Join(",",Presentation.Apply(raw,p).Fields.Select(f=>f.Name)));
    p.ToggleSort(raw.Fields[1].Key); p.MoveSortsToFront();
    Equal("c,a,id,b",string.Join(",",Presentation.Apply(raw,p).Fields.Select(f=>f.Name)));
});
Test("Response filters remove records from every layout before files combine",()=> {
    var s=Examples.CompoundFixture(); s.Sources[1].KeepRules.Add(new(1,["Mother","Father"],false));
    var longView=Engine.Build(s,Layout.PersonTime); int person=longView.Fields.FindIndex(f=>f.Key=="@entity");
    Equal(false,longView.Rows.Any(r=>r[person].Value=="Child")); Equal(true,longView.Rows.Any(r=>r[person].Value=="Mother"));
    Equal(false,Engine.Build(s,Layout.Person).Fields.Any(f=>f.Name.Contains("__Child",StringComparison.Ordinal)));
    Equal(1,s.Copy().Sources[1].KeepRules.Count); Equal(false,Engine.SourceView(s.Sources[1]).Rows.Any(r=>r[1].Value=="Child"));
    var plan=Recipe.Plan(s,Layout.PersonTime,MatchMode.AllPeople,false,null,new(),"");
    string r=Recipe.R(plan).Replace("\n    "," "), sas=SasRecipe.Generate(plan).Replace("\n    "," ");
    Equal(true,r.Contains("%in% c(\"Mother\", \"Father\"), ]")); Equal(true,r.Contains("# leaves out \"Child\""));
    Equal(true,sas.Contains("if not (person in ('Mother', 'Father')) then delete;"));
});
Test("Mostly numeric text columns are offered as numbers and the change can be reverted",()=> {
    var s=Csv.Import("id,age,code,group\n1,30,001,a\n2,not given,002,b\n3,41,003,c\n4,,004,d\n5,22,005,e\n6,35,006,f\n7,29,007,g\n8,50,008,h\n9,61,009,i\n10,44,010,j\n11,38,011,k","detect.csv");
    var found=NumericDetection.Find(With(s));
    Equal(true,found.Any(c=>c.Column==1 && c.Codes.SequenceEqual(new[]{"not given"}))); Equal(false,found.Any(c=>c.Column is 2 or 3)); Equal(true,found.Any(c=>c.Column==0));
    NumericDetection.Apply(found.Where(c=>c.Column==1));
    Equal(ValueKind.Number,s.Fields[1].Kind); Equal("",s.Rows[1][1]); Equal(true,s.IsMissing(s.Rows[1][1],1)); Equal("30",s.Rows[0][1]);
    var plan=Recipe.Plan(With(s),Layout.Source,MatchMode.AllPeople,false,null,new(),""); Equal(true,Recipe.R(plan).Contains("\"not given\" counts as missing"));
    NumericDetection.Revert(s,1); Equal(ValueKind.Text,s.Fields[1].Kind); Equal("not given",s.Rows[1][1]); Equal(0,s.Steps.Count); Equal(null,s.Fields[1].MissingTokens);
    Throws(()=>NumericDetection.Revert(s,1));
});
Test("Named exports prefix every file and include the chosen software's cleaning script",()=> {
    var s=Examples.All[0].Create(); var view=Engine.Build(s,Layout.PersonTime); Directory.CreateDirectory(args[0]);
    var plan=Recipe.Plan(s,Layout.PersonTime,MatchMode.AllPeople,false,null,new(),"");
    string zip=Path.Combine(args[0],"named.zip"); Export.SaveBundles(zip,new Dictionary<string,View>{[""]=view},ExportTarget.R,"study",new Dictionary<string,string>{[""]=Recipe.R(plan,false,"study")});
    using(var a=ZipFile.OpenRead(zip)) {
        Equal("study README.txt,study clean.R,study dictionary.csv,study import.R,study.csv",string.Join(",",a.Entries.Select(e=>e.FullName).OrderBy(x=>x,StringComparer.Ordinal)));
        using var reader=new StreamReader(a.GetEntry("study import.R")!.Open()); Equal(true,reader.ReadToEnd().Contains("\"study.csv\""));
    }
    Equal("study.csv,study dictionary.csv,study import.sas,study clean.sas,study README.txt",string.Join(",",Export.BundleFiles("study",ExportTarget.Sas,true)));
    Equal("st_dy",Export.CleanName("st/dy")); Equal("prepared_data",Export.CleanName("  ")); Equal("study_wide",Export.ShapeName("study","wide"));
});
string? rscript=Environment.GetEnvironmentVariable("WIDELONG_RSCRIPT");
if(!string.IsNullOrWhiteSpace(rscript) && File.Exists(rscript))
{
    void RunRCase(string name, Session s, Layout layout, bool stack=false, MatchMode match=MatchMode.AllPeople, string filter="", bool edits=false, string[]? rowNames=null, RowFilterSpec? rules=null, bool allowUnsupportedSas=false)
        => RReplay.Run(args[0], rscript!, name, s, layout, stack, match, filter, rowNames, rules, allowUnsupportedSas);
    Test("Native R reproduces numeric IDs, numeric ID sorting and upper-case text ordering", () =>
    {
        var paddedA=NumericIds("id,visit,label\n00010,m0,a\n00002,m0,b\n00001,m0,c\n00002,m3,d","padded_a.csv",0); paddedA.TimeColumn=1;
        var paddedB=NumericIds("id,age\n00010,40\n00002,30\n00001,20","padded_b.csv",0);
        RunRCase("leading_zero_ids_long",With(paddedA,paddedB),Layout.PersonTime,edits:true);
        RunRCase("leading_zero_ids_wide",With(paddedA,paddedB),Layout.Person,edits:true);
        var a=NumericIds("id,visit,label\n10,m0,a_b\n2,m0,ab\n1,m0,Ab\n2,m3,a_c","numeric_a.csv",0); a.TimeColumn=1;
        var b=NumericIds("id,age\n10,40\n2,30\n1,20","numeric_b.csv",0);
        RunRCase("numeric_ids_long",With(a,b),Layout.PersonTime,edits:true);
        RunRCase("numeric_ids_wide",With(a,b),Layout.Person,edits:true);
    });
    Test("Native R reproduces all example structures, layouts, sorting and column order", () =>
    {
        for(int i=0;i<Examples.All.Count;i++) foreach(var layout in new[]{Layout.PersonTime,Layout.Person,Layout.Measurement,Layout.Source})
        {
            var example=Examples.All[i]; if(example.Stack && layout==Layout.Person)continue;
            RunRCase("example"+(i+1)+"_"+layout,example.Create(),layout,example.Stack);
        }
    });
    Test("Native R replays messy recodes, type conversions and individual corrections", () =>
    {
        var s=Examples.All[4].Create(); var source=s.Sources[0];
        source.Steps.Add(new(StepKind.Replace,1,Before:"not given",After:"")); foreach(var row in source.Rows)if(row[1]=="not given")row[1]="";
        source.Steps.Add(new(StepKind.Convert,1,Target:ValueKind.Number)); source.Fields[1].Kind=ValueKind.Number;
        string before=source.Rows[0][1]; source.Steps.Add(new(StepKind.Cell,1,0,before,"35"));source.Rows[0][1]="35";
        source.Steps.Add(new(StepKind.Convert,2,Target:ValueKind.Boolean));foreach(var row in source.Rows)row[2]=Values.Convert(row[2],ValueKind.Boolean);source.Fields[2].Kind=ValueKind.Boolean;
        for(int c=4;c<=5;c++){source.Steps.Add(new(StepKind.Replace,c,Before:"NA",After:""));foreach(var row in source.Rows)if(row[c]=="NA")row[c]="";source.Steps.Add(new(StepKind.Convert,c,Target:ValueKind.Number));source.Fields[c].Kind=ValueKind.Number;}
        RunRCase("cleaned_survey",s,Layout.PersonTime,filter:"Urban");
    });
    Test("Native R replays explicit dates and safely quoted corrections",()=>
    {
        var source=Csv.Import("id,visit,date,amount,note\n001,Baseline,03/04/2025,001.20,original\n002,Baseline,12/25/2025,2.50,original", "dates.csv");
        source.Steps.Add(new(StepKind.Convert,2,Target:ValueKind.Date,DateFormat:"MM/dd/yyyy"));foreach(var row in source.Rows)row[2]=Values.Convert(row[2],ValueKind.Date,"MM/dd/yyyy");source.Fields[2].Kind=ValueKind.Date;
        source.Steps.Add(new(StepKind.Convert,3,Target:ValueKind.Number));foreach(var row in source.Rows)row[3]=Values.Convert(row[3],ValueKind.Number);source.Fields[3].Kind=ValueKind.Number;
        string replacement="quote ' double \" slash \\ % &";source.Steps.Add(new(StepKind.Replace,4,Before:"original",After:replacement));foreach(var row in source.Rows)row[4]=replacement;
        RunRCase("dates_quotes",With(source),Layout.PersonTime);
    });
    Test("Native R matching rules and empty output reproduce the preview",()=>
    {
        var a=Csv.Import("id,age\n001,25\n002,30", "people.csv");var b=Csv.Import("id,visit,score\n001,m0,4\n001,m3,5\n003,m0,6", "visits.csv");
        RunRCase("common_people",With(a,b),Layout.PersonTime,match:MatchMode.CommonPeople);RunRCase("first_file",With(a,b),Layout.PersonTime,match:MatchMode.FirstFilePeople);RunRCase("no_rows",With(a,b),Layout.PersonTime,filter:"no matching value");
    });
    Test("Native R respects delimiter/encoding options and empty source files",()=>
    {
        var semi=Csv.Import("id;name;value\n001;André;2\n002;Zoë;3", "encoded.csv", ';');semi.EncodingName="windows-1252";
        RunRCase("semicolon_cp1252",With(semi),Layout.Source);
        var tab=Csv.Import("id\tname\tvalue\n001\tAndré\t2\n002\tZoë\t3", "utf16.tsv", '\t');tab.EncodingName="UTF-16LE";
        RunRCase("tab_utf16",With(tab),Layout.PersonTime);
        RunRCase("empty_source",With(Csv.Import("id,value\n", "empty.csv")),Layout.PersonTime);
    });
    Test("Native R reproduces chosen row variables while retaining context columns",()=>
    {
        RunRCase("only_weight_rows",Demo.Create(20),Layout.Measurement,rowNames:["weight_kg"]);
        RunRCase("two_row_variables",Demo.Create(20),Layout.Measurement,rowNames:["weight_kg","heart_rate"]);
        RunRCase("explicit_flat_rows",With(Csv.Import("id,name,age\n001,Avery,25\n002,Jordan,30", "flat.csv")),Layout.Measurement,rowNames:["name"]);
    });    Test("Native R preserves explicit missing tokens, true empty text and literal NA",()=> {
        var source=Csv.Import("id,text,amount,flag,date\n1,NA,MISS,true,2025-01-01\n2,,2,,\n3,MISS,3,false,2025-01-03", "policy.csv");
        source.MissingTokens=["MISS"];
        source.Steps.Add(new(StepKind.Convert,2,Target:ValueKind.Number,MissingTokens:["MISS"]));foreach(var row in source.Rows)row[2]=Values.ConvertWithMissing(row[2],ValueKind.Number,"yyyy-MM-dd",["MISS"]);source.Fields[2].Kind=ValueKind.Number;
        source.Fields[3].Kind=ValueKind.Boolean;source.Fields[4].Kind=ValueKind.Date;
        RunRCase("missing_policy",With(source),Layout.PersonTime);
        RunRCase("missing_policy_source",With(source),Layout.Source);
        var a=Csv.Import("id,x_m0,x_m3,x_m6\n1,4,5,", "wide.csv");a.Fields[2].Keep=false;
        var b=Csv.Import("id,visit,y\n1,m0,a\n1,m3,b\n1,m6,c", "visits.csv");RunRCase("selected_occasions",With(a,b),Layout.Person);
    });

    Test("Native R repeats variable-scoped missing rules in source and combined layouts",()=> {
        RunRCase("column_missing_long",MissingRulesChecks.Fixture(),Layout.PersonTime);
        RunRCase("column_missing_source",MissingRulesChecks.Fixture(),Layout.Source);
    });
    Test("Native R matches typed AND/OR rules, missing checks and filtered export shapes",()=> {
        var source=Csv.Import("id,visit,amount,status,date,flag\n1,m0,10,Complete,2025-01-01,true\n1,m3,20,Pending,2025-03-01,false\n2,m0,30,Complete,2025-02-01,true\n2,m3,,Complete,,", "rules.csv");source.Fields[2].Kind=ValueKind.Number;source.Fields[4].Kind=ValueKind.Date;source.Fields[5].Kind=ValueKind.Boolean;
        var data=With(source);var longView=Engine.Build(data,Layout.PersonTime);
        RowFilterRule Rule(string name,FilterOperator op,string value="",string upper="") {var field=longView.Fields.Single(f=>f.Name==name);return new(field.Key,field.Name,field.Kind,op,value,upper);}
        RunRCase("rules_and",data,Layout.PersonTime,rules:new(){Rules=[Rule("amount",FilterOperator.GreaterEqual,"20"),Rule("status",FilterOperator.Equal,"Complete")]});
        RunRCase("rules_or",data,Layout.PersonTime,rules:new(){MatchAll=false,Rules=[Rule("amount",FilterOperator.Less,"15"),Rule("amount",FilterOperator.IsMissing)]});
        RunRCase("rules_dates_bool",data,Layout.PersonTime,rules:new(){Rules=[Rule("date",FilterOperator.Between,"2025-01-15","2025-03-01"),Rule("flag",FilterOperator.Equal,"yes")]});
        var wide=Engine.Build(data,Layout.Person);var wf=wide.Fields.Single(f=>f.Name=="amount__m0");RunRCase("rules_wide",data,Layout.Person,rules:new(){Rules=[new(wf.Key,wf.Name,wf.Kind,FilterOperator.Greater,"15")]});
        RunRCase("rules_with_search",data,Layout.PersonTime,filter:"Complete",rules:new(){Rules=[Rule("amount",FilterOperator.Greater,"15")]});
        var empty=Csv.Import("id,label\n1,\n2,MISS\n3,NA", "empty_rules.csv");empty.Fields[1].MissingTokens=["MISS"];var ev=Engine.SourceView(empty);var ef=ev.Fields[1];
        RunRCase("rules_true_empty",With(empty),Layout.Source,rules:new(){Rules=[new(ef.Key,ef.Name,ef.Kind,FilterOperator.Equal,"")]},allowUnsupportedSas:true);
    });
    Test("Native R replays explicitly mapped repeated columns",()=> {
        var source=Csv.Import("id,temperature_january,temperature_february\nA,20,21\nB,18,19", "mapped.csv");var maps=RepeatedColumnMapping.FromSource(source);foreach(var map in maps){map.Variable="temperature";map.Grouping=RepeatedColumnMapping.InferGrouping(map.Name);}RepeatedColumnMapping.Apply(source,maps);
        RunRCase("mapped_months_long",With(source),Layout.PersonTime);
        RunRCase("mapped_months_wide",With(source),Layout.Person);
    });
    Test("Native R applies response filters and detected numbers like the application",()=> {
        var s=Examples.CompoundFixture(); s.Sources[1].KeepRules.Add(new(1,["Mother","Father"],false)); RunRCase("response_filter_long",s,Layout.PersonTime);
        var w=Examples.CompoundFixture(); w.Sources[1].KeepRules.Add(new(1,["Mother","Father"],false)); RunRCase("response_filter_wide",w,Layout.Person);
        var survey=Examples.All[4].Create(); NumericDetection.Apply(NumericDetection.Find(survey)); RunRCase("survey_detected_numbers",survey,Layout.PersonTime);
    });
    Test("Native R preserves compound entities when stacking and broadcasting entity-free time rows",()=> {
        RunRCase("compound_stacked",Examples.CompoundFixture(),Layout.PersonTime,stack:true);
        var wide=Csv.Import("id,person,nickname,score_m0,score_m3\nA,Patient,Pat,10,11\nA,Spouse,Sue,20,21", "role_scores.csv");wide.EntityColumn=1;
        var times=Csv.Import("id,time,calendar_status\nA,m0,Start\nA,m3,Later", "calendar.csv");
        RunRCase("compound_entity_free_time",With(wide,times),Layout.PersonTime);
    });
    Test("Native R repeats key type changes (dates, padded numbers, filtered and partially converted keys)",()=> {
        RunRCase("date_time_key_long",RegressionChecks.DateKeyFixture(),Layout.PersonTime);
        RunRCase("date_time_key_wide",RegressionChecks.DateKeyFixture(),Layout.Person);
        RunRCase("padded_time_key_wide",RegressionChecks.PaddedTimeFixture(),Layout.Person);
        RunRCase("filtered_converted_key",RegressionChecks.FilteredKeyFixture(),Layout.PersonTime);
        RunRCase("partial_id_conversion",RegressionChecks.PartialIdFixture(),Layout.PersonTime);
        RunRCase("replace_on_filtered_role",RegressionChecks.ReplacedFilterFixture(),Layout.PersonTime);
    });
    Test("Native R reproduces scale scores",()=> {
        RunRCase("scale_scores_source",FeatureChecks.ScaleFixture(),Layout.Source);
        RunRCase("scale_scores_long",FeatureChecks.ScaleFixture(),Layout.PersonTime);
    });
    Test("Native R skips Qualtrics description rows",()=> {
        RunRCase("qualtrics_preset",FeatureChecks.QualtricsFixture(),Layout.Source,allowUnsupportedSas:true);
    });
    Test("Native R readers retain single-column empty records and Unicode",()=> {
        var source=Csv.Import("value\n\n\"\"\nα\nNA\n\"hello\nworld\"\n", "single.csv");source.MissingTokens=[];
        RunRCase("single_column_text",With(source),Layout.Source);
    });
    Test("Native R imports both-layout bundles and preserves typed missing values",()=> {
        void VerifyBundle(string name, View view, ExportTarget target, string assertions) {
            string folder=Path.Combine(args[0],"bundle-cases",name);Directory.CreateDirectory(folder);
            string zipPath=Path.Combine(args[0],name+".zip");Export.SaveBundle(zipPath,view,target);ZipFile.ExtractToDirectory(zipPath,folder,true);
            File.WriteAllText(Path.Combine(folder,"verify.R"),"source('import.R',encoding='UTF-8')\n"+assertions+"\ncat('PASS native bundle reader\n')\n");
            var start=new ProcessStartInfo(rscript!) {WorkingDirectory=folder,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};start.ArgumentList.Add("--vanilla");start.ArgumentList.Add("verify.R");start.Environment["LC_ALL"]="C";
            using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(30000)){process.Kill(true);throw new Exception("Bundle reader timed out");}Task.WaitAll(stdout,stderr);if(process.ExitCode!=0)throw new Exception(name+" "+stdout.Result+stderr.Result);
        }
        var single=Csv.Import("value\n\"\"\nα\nNA\nMISSING\n", "single.csv");single.MissingTokens=["MISSING"];
        VerifyBundle("empty_unicode_literal",Engine.SourceView(single),ExportTarget.R,"stopifnot(nrow(data)==4,identical(data[[1]][1],''),identical(enc2utf8(data[[1]][2]),intToUtf8(945)),identical(data[[1]][3],'NA'),is.na(data[[1]][4]))");
        var source=Csv.Import("id,n,date,flag,text\n001,2.5,2025-01-01,true,literal\n002,,,,", "typed.csv");source.Fields[1].Kind=ValueKind.Number;source.Fields[2].Kind=ValueKind.Date;source.Fields[3].Kind=ValueKind.Boolean;
        foreach(var target in new[]{ExportTarget.R,ExportTarget.Sas}) VerifyBundle("typed_"+target,Engine.SourceView(source),target,"stopifnot(identical(data$id,c('001','002')),identical(data$n,c(2.5,NA_real_)),inherits(data$date,'Date'),is.na(data$date[2]),identical(data$flag,c(TRUE,NA)),is.na(data$text[2]))");
        var data=Demo.Create(3);string bothPath=Path.Combine(args[0],"both-native.zip");Export.SaveBundles(bothPath,new Dictionary<string,View> { ["wide"]=Engine.Build(data,Layout.Person),["long"]=Engine.Build(data,Layout.PersonTime) });
        string bothFolder=Path.Combine(args[0],"both-native");ZipFile.ExtractToDirectory(bothPath,bothFolder,true);
        foreach(var shape in new[]{"wide","long"}) {string folder=Path.Combine(bothFolder,shape);var info=new ProcessStartInfo(rscript!) { WorkingDirectory=folder,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true };info.ArgumentList.Add("--vanilla");info.ArgumentList.Add("-e");info.ArgumentList.Add("source('import.R');stopifnot(nrow(data)=="+(shape=="wide"?3:9)+")");using var process=Process.Start(info)!;var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();if(!process.WaitForExit(30000)){process.Kill(true);throw new Exception("Both export timed out");}Task.WaitAll(output,error);if(process.ExitCode!=0)throw new Exception(error.Result);}
    });
}
else Console.WriteLine("SKIP native R integration: pass Build.ps1 -Rscript <installed Rscript.exe> to execute generated cleaning code.");
Console.WriteLine($"\n{passed} meaningful checks passed. Synthetic data only.");
