using WideLong;

public static class ExportReviewChecks
{
    public static void Run(Action<string,Action> test,Action<bool> check)
    {
        test("Export review matches serialized names, codes and dimensions without mutating the preview",()=>
        {
            var source=Csv.Import("id,long variable name,amount\n001,NA,\n002,,2", "review.csv");source.Fields[2].Kind=ValueKind.Number;
            var view=Engine.SourceView(source);var review=Export.Review(view,ExportTarget.R,true);
            check(review.Rows==2&&review.Columns==3&&review.MissingValues==2);check(review.Files.Count==5);
            check(review.RenamedColumns.Single().ExportName=="long_variable_name");check(review.Sample.Fields[1].Name=="long_variable_name");
            check(review.Sample.Rows[0][1].Value=="NA");check(review.Sample.Rows[0][2].Value!="NA"&&review.Sample.Rows[0][2].Value.Length>0);
            check(view.Fields[1].Name=="long variable name"&&view.Rows[0][2].Missing);
            var sas=Export.Review(view,ExportTarget.Sas,true);check(sas.Sample.Rows[0][2].Value=="."&&sas.Sample.Rows[1][1].Value=="");
            var csv=Export.Review(view,ExportTarget.Generic,false);check(csv.RenamedColumns.Count==0&&csv.Sample.Rows[0][2].Value=="");
        });
        test("Export review blocks invalid bundles before the destination is chosen",()=>
        {
            bool rejected=false;try{Export.Review(Engine.SourceView(Csv.Import("id,note\n1,\"two\nlines\"", "multiline.csv")),ExportTarget.R,true);}catch(InvalidOperationException){rejected=true;}check(rejected);
            rejected=false;try{Export.Review(Engine.SourceView(Csv.Import("id,note\n1,NA", "literal.csv")),ExportTarget.R,false);}catch(InvalidOperationException){rejected=true;}check(rejected);
            var empty=Export.Review(Engine.SourceView(Csv.Import("id,note\n", "empty.csv")),ExportTarget.R,true);check(empty.Rows==0&&empty.Sample.Rows.Count==0&&empty.Notices.Any(n=>n.Contains("no data rows")));
        });
    }
}
