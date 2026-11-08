namespace WideLong;

public sealed record ExportColumnName(string DisplayName, string ExportName);
public sealed record ExportReviewInfo(int Rows, int Columns, long MissingValues, string MissingConvention, View Sample, List<ExportColumnName> RenamedColumns, List<string> Files, List<string> Notices);

public static partial class Export
{
    public static ExportReviewInfo Review(View view, ExportTarget target, bool bundle, string? name = null, bool cleaning = false)
    {
        string token = MissingToken(view, target, bundle);
        if (bundle) ValidateBundle(view, token);
        var names = bundle ? SasNames(view) : view.Fields.Select(f => f.Name).ToList();
        var sample = new View
        {
            Fields = view.Fields.Select((f,i) => f with { Name = names[i] }).ToList(),
            Rows = view.Rows.Take(8).Select(row => row.Select((cell,i) => new Cell(Encoded(cell,view.Fields[i],target,token), MissingFlag:false)).ToArray()).ToList()
        };
        var changes = view.Fields.Select((f,i) => new ExportColumnName(f.Name,names[i])).Where(n => n.DisplayName != n.ExportName).ToList();
        string convention = target == ExportTarget.Sas ? "SAS: numeric, Date and Boolean missing = .; character missing = blank."
            : token.Length == 0 ? "Missing values = blank cells. CSV alone does not distinguish missing from empty text."
            : "Missing values = " + token + (bundle ? ". The supplied R reader converts this to typed NA and preserves literal text codes." : ". Read these as NA in R; CSV alone has no type metadata.");
        var notices = new List<string>();
        if (target == ExportTarget.Sas) notices.Add("SAS character blanks cannot distinguish genuine empty text from missing text.");
        if (bundle && token != "NA" && target != ExportTarget.Sas) notices.Add("A collision-free missing code is used because NA already appears as literal text. Use the included import scripts.");
        if (!bundle) notices.Add("Column types are not stored in a CSV file. Choose ZIP to include a dictionary and typed import scripts.");
        if (view.Rows.Count == 0) notices.Add("This export has column headers and no data rows.");
        return new(view.Rows.Count,view.Fields.Count,view.Rows.Sum(row => (long)row.Count(c=>c.Missing)),convention,sample,changes,
            bundle ? BundleFiles(name, target, cleaning) : [(name ?? "selected_filename") + ".csv"],notices);
    }
}
