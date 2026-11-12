namespace WideLong;

public sealed record ExportColumnName(string DisplayName, string ExportName);
public sealed record ExportReviewInfo(int Rows, int Columns, long MissingValues, string MissingConvention, View Sample, List<ExportColumnName> RenamedColumns, List<string> Files, List<string> Notices)
{
    /// <summary>Per-variable summary of the exported values (N, missing, distinct, range, mean, flags).</summary>
    public List<VariableSummary> Summaries { get; init; } = [];
}
/// <param name="Outliers">Numbers more than 3 interquartile ranges below the first or above the third quartile.</param>
public sealed record VariableSummary(string Name, ValueKind Kind, int N, int Missing, int Distinct, string? Min, string? Max, double? Mean, int Outliers, string Flags)
{
    public double MissingShare => N + Missing == 0 ? 0 : (double)Missing / (N + Missing);
}

public static partial class Export
{
    public static ExportReviewInfo Review(View view, ExportTarget target, bool bundle, string? name = null, bool cleaning = false)
    {
        string token = MissingToken(view, target, bundle);
        if (bundle) ValidateBundle(view, token);
        var names = bundle && !(name != null && target == ExportTarget.R) ? SasNames(view) : view.Fields.Select(f => f.Name).ToList();
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
            bundle ? BundleFiles(name, target, cleaning) : [(name ?? "selected_filename") + ".csv"],notices) { Summaries = Summarize(view) };
    }

    /// <summary>N, missing, distinct values, minimum/maximum, mean and simple flags for every exported column.</summary>
    public static List<VariableSummary> Summarize(View view)
    {
        var result = new List<VariableSummary>();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        for (int c = 0; c < view.Fields.Count; c++)
        {
            var field = view.Fields[c];
            var present = view.Rows.Select(r => r[c]).Where(x => !x.Missing).Select(x => x.Value).ToList();
            int missing = view.Rows.Count - present.Count, distinct = present.Distinct(StringComparer.Ordinal).Count();
            string? min = null, max = null; double? mean = null; int outliers = 0;
            var flags = new List<string>();
            if (field.Kind == ValueKind.Number)
            {
                var numbers = present.Select(v => double.TryParse(v, System.Globalization.NumberStyles.Float, invariant, out double d) ? d : double.NaN).Where(d => !double.IsNaN(d)).OrderBy(d => d).ToList();
                if (numbers.Count > 0)
                {
                    min = numbers[0].ToString("G15", invariant); max = numbers[^1].ToString("G15", invariant); mean = numbers.Average();
                    if (numbers.Count >= 8)
                    {
                        double Quantile(double p) { double at = p * (numbers.Count - 1); int lo = (int)Math.Floor(at); return numbers[lo] + (numbers[Math.Min(lo + 1, numbers.Count - 1)] - numbers[lo]) * (at - lo); }
                        double q1 = Quantile(0.25), q3 = Quantile(0.75), iqr = q3 - q1;
                        if (iqr > 0) outliers = numbers.Count(d => d < q1 - 3 * iqr || d > q3 + 3 * iqr);
                    }
                }
            }
            else if (present.Count > 0)
            {
                var ordered = present.Order(StringComparer.Ordinal).ToList(); min = ordered[0]; max = ordered[^1];
            }
            if (present.Count == 0 && view.Rows.Count > 0) flags.Add("all missing");
            else if (view.Rows.Count > 0 && missing * 2 > view.Rows.Count) flags.Add("over half missing");
            if (distinct == 1 && view.Rows.Count > 1) flags.Add("one value only");
            if (outliers > 0) flags.Add($"{outliers:N0} far from the rest (beyond 3×IQR)");
            result.Add(new(field.Name, field.Kind, present.Count, missing, distinct, min, max, mean, outliers, string.Join("; ", flags)));
        }
        return result;
    }
}
