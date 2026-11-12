using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace WideLong;

public enum ExportTarget { Generic, R, Sas }

public static partial class Export
{
    private static string MissingToken(View view, ExportTarget target, bool bundle) {
        if (target == ExportTarget.Sas) return ".";
        if (!bundle && target == ExportTarget.Generic) return "";
        string token = "NA";
        bool Collision(string value) => view.Rows.Any(r => r.Any(c => !c.Missing && c.Value == value));
        if (!bundle && Collision(token)) throw new InvalidOperationException("This dataset contains literal NA text. Choose the ZIP with import scripts to preserve both literal NA and missing values.");
        while (Collision(token)) token = "_" + token + "_";
        return token;
    }
    private static string Encoded(Cell cell, OutputField field, ExportTarget target, string token) => !cell.Missing ? cell.Value : target == ExportTarget.Sas && field.Kind == ValueKind.Text ? "" : token;
    private static void WriteData(TextWriter writer, View view, ExportTarget target, string token) {
        writer.WriteLine(string.Join(",", view.Fields.Select(f => Csv.Escape(f.Name))));
        foreach (var row in view.Rows) writer.WriteLine(string.Join(",", row.Select((c,i) => Csv.Escape(Encoded(c,view.Fields[i],target,token)))));
    }
    public static void SaveCsv(string path, View view, ExportTarget target = ExportTarget.Generic)
    {
        string token = MissingToken(view, target, false);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        WriteData(writer, view, target, token);
    }

    public static List<string> SasNames(View view)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var f in view.Fields)
        {
            string name = Regex.Replace(f.Name, "[^A-Za-z0-9_]", "_");
            if (name.Length == 0 || !Regex.IsMatch(name[..1], "[A-Za-z_]")) name = "v_" + name;
            if (name.Equals("_N_", StringComparison.OrdinalIgnoreCase) || name.Equals("_ERROR_", StringComparison.OrdinalIgnoreCase)) name = "v" + name;
            name = name[..Math.Min(name.Length, 32)];
            string root = name; int n = 2;
            while (!used.Add(name)) { string suffix = "_" + n++; name = root[..Math.Min(root.Length, 32 - suffix.Length)] + suffix; }
            names.Add(name);
        }
        return names;
    }

    // ---- Value labels (1 = "Strongly disagree") ----
    /// <summary>Labelled values in a stable order: numbers numerically, text ordinally.</summary>
    public static List<KeyValuePair<string, string>> OrderedLabels(OutputField field) => field.Labels == null ? [] :
        field.Kind == ValueKind.Number
            ? field.Labels.OrderBy(p => decimal.TryParse(p.Key, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : decimal.MaxValue).ThenBy(p => p.Key, StringComparer.Ordinal).ToList()
            : field.Labels.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
    public static bool HasLabels(OutputField field) => field.Labels is { Count: > 0 } && field.Kind is ValueKind.Number or ValueKind.Text;
    public static string DescribeLabels(OutputField field) => string.Join("; ", OrderedLabels(field).Select(p => p.Key + " = " + p.Value));
    /// <summary>R named vector c("Strongly disagree" = 1, ...), the labelled-vector convention haven/labelled read.</summary>
    public static string RLabelVector(OutputField field) => "c(" + string.Join(", ", OrderedLabels(field).Select(p => Recipe.RQuote(p.Value) + " = " + (field.Kind == ValueKind.Number ? p.Key : Recipe.RQuote(p.Key)))) + ")";
    /// <summary>SAS format names wla, wlb, … (letters only: format names cannot end in a digit).</summary>
    public static string SasFormatName(int index, bool text) { string letters = ""; int n = index; do { letters = (char)('a' + n % 26) + letters; n = n / 26 - 1; } while (n >= 0); return (text ? "$" : "") + "wl" + letters; }
    /// <summary>A PROC FORMAT step for the labelled columns; returns the format name per column index.</summary>
    public static (string Code, Dictionary<int, string> Formats) SasFormats(IReadOnlyList<OutputField> fields)
    {
        var formats = new Dictionary<int, string>(); var b = new StringBuilder();
        for (int i = 0; i < fields.Count; i++)
        {
            if (!HasLabels(fields[i])) continue;
            string name = SasFormatName(formats.Count, fields[i].Kind == ValueKind.Text); formats[i] = name;
            b.Append($"  value {name} ").Append(string.Join(" ", OrderedLabels(fields[i]).Select(p => (fields[i].Kind == ValueKind.Number ? p.Key : Recipe.SasQuote(p.Key)) + " = " + Recipe.SasQuote(p.Value)))).Append(";\n");
        }
        return (formats.Count == 0 ? "" : "proc format;\n" + b + "run;\n", formats);
    }

    public static void SaveBundle(string path, View view, ExportTarget target = ExportTarget.R) => SaveBundles(path, new Dictionary<string, View> { [""] = view }, target);

    /// <summary>File names inside an export. With a dataset name every file starts with it ("NAME.csv", "NAME import.R");
    /// without one the original generic names are used. Only the chosen software's scripts are included.</summary>
    public static List<string> BundleFiles(string? name, ExportTarget target, bool cleaning = false)
    {
        if (name == null) return ["data.csv", "dictionary.csv", "import.R", "import.sas", "README.txt"];
        var files = new List<string> { name + ".csv", name + " dictionary.csv" };
        if (target == ExportTarget.R) { files.Add(name + " import.R"); if (cleaning) files.Add(name + " clean.R"); }
        if (target == ExportTarget.Sas) { files.Add(name + " import.sas"); if (cleaning) files.Add(name + " clean.sas"); }
        files.Add(name + " README.txt");
        return files;
    }
    /// <summary>A dataset name usable in file names: Windows-invalid characters removed, never empty.</summary>
    public static string CleanName(string name)
    {
        string clean = Regex.Replace(name.Trim(), @"[\\/:*?""<>|\x00-\x1f]", "_").Trim().TrimEnd('.');
        return clean.Length == 0 ? "prepared_data" : clean.Length > 80 ? clean[..80].TrimEnd() : clean;
    }
    /// <summary>The dataset name for one layout of a "both" export, e.g. "study_wide".</summary>
    public static string ShapeName(string name, string shapeKey) => shapeKey.Length == 0 ? name : name + "_" + shapeKey;

    public static void SaveBundles(string path, IReadOnlyDictionary<string, View> views, ExportTarget target = ExportTarget.R, string? name = null, IReadOnlyDictionary<string, string>? cleaning = null)
    {
        // Prepare every entry before opening the destination, including both layouts.
        var entries = new Dictionary<string, string>();
        foreach (var pair in views) {
            string prefix = pair.Key.Length == 0 ? "" : pair.Key + "/";
            string? shapeName = name == null ? null : ShapeName(name, pair.Key);
            foreach (var entry in BundleEntries(pair.Value, target, shapeName, cleaning?.GetValueOrDefault(pair.Key))) entries.Add(prefix + entry.Key, entry.Value);
        }
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var entry in entries) { using var writer = new StreamWriter(zip.CreateEntry(entry.Key).Open(), new UTF8Encoding(false)); writer.Write(entry.Value); }
    }
    private static Dictionary<string, string> BundleEntries(View view, ExportTarget target, string? name = null, string? cleaning = null)
    {
        var files = BundleFiles(name, target, cleaning != null);
        // Validate the complete bundle before opening the selected destination.
        string token = MissingToken(view, target, true);
        ValidateBundle(view, token);
        // R reads the CSV with check.names = FALSE, so an R export keeps the display names that clean.R also produces.
        // SAS (and the legacy two-script bundle) need SAS-compatible names.
        bool displayNames = name != null && target == ExportTarget.R;
        var sasNames = SasNames(view);
        var names = displayNames ? view.Fields.Select(f => f.Name).ToList() : sasNames;
        var lengths = new int[view.Fields.Count];
        for (int i = 0; i < lengths.Length; i++)
        {
            lengths[i] = Math.Max(Math.Max(1, Encoding.UTF8.GetByteCount(token)), view.Rows.Select(r => Encoding.UTF8.GetByteCount(r[i].Value)).DefaultIfEmpty(1).Max());
            if (lengths[i] > 32767) throw new InvalidOperationException("SAS companion scripts support at most 32,767 UTF-8 bytes per value. Use CSV-only export for this dataset.");
        }
        if (view.Rows.Any(r => r.Any(c => c.Value.Contains('\n') || c.Value.Contains('\r'))))
            throw new InvalidOperationException("CSV export supports embedded line breaks. The SAS companion script does not yet support multiline values; choose CSV-only export.");
        long lrecl = Math.Max(32767, view.Rows.Select(row => row.Select((cell, i) => (long)Encoding.UTF8.GetByteCount(Csv.Escape(Encoded(cell,view.Fields[i],target,token)))).Sum() + row.Length + 8).DefaultIfEmpty(32767).Max());
        var entries = new Dictionary<string, string>();
        void Entry(string name, Action<TextWriter> write) { using var writer = new StringWriter(CultureInfo.InvariantCulture); write(writer); entries.Add(name, writer.ToString()); }
        var compatible = new View { Fields = view.Fields.Select((f, i) => f with { Name = names[i] }).ToList(), Rows = view.Rows };
        Entry(files[0], w => WriteData(w, compatible, target, token));
        Entry(files[1], w =>
        {
            bool labelled = view.Fields.Any(HasLabels);
            w.WriteLine("export_name,display_name,type,source" + (labelled ? ",value_labels" : ""));
            for (int i = 0; i < names.Count; i++) w.WriteLine(string.Join(",", new[] { names[i], view.Fields[i].Name, view.Fields[i].Kind.ToString(), view.Fields[i].Source }.Concat(labelled ? [HasLabels(view.Fields[i]) ? DescribeLabels(view.Fields[i]) : ""] : []).Select(Csv.Escape)));
        });
        if (name == null || target == ExportTarget.R) Entry(files.First(f => f.EndsWith("import.R", StringComparison.Ordinal)), w => w.Write(RScript(view, target, token, files[0])));
        if (name == null || target == ExportTarget.Sas) Entry(files.First(f => f.EndsWith("import.sas", StringComparison.Ordinal)), w => w.Write(SasScript(view, sasNames, lengths, lrecl, target, token, files[0])));
        if (cleaning != null) Entry(files.First(f => f.Contains(" clean.", StringComparison.Ordinal)), w => w.Write(cleaning));
        if (name != null)
        {
            string script = target == ExportTarget.R ? name + " import.R" : target == ExportTarget.Sas ? name + " import.sas" : "";
            var lines = new List<string> { $"{name} — WideLong {CodeModel.Version} export", "", $"{name}.csv — the prepared dataset (" + (displayNames ? "column names as shown in WideLong" : "column names are SAS-compatible; see the dictionary") + ")", $"{name} dictionary.csv — each column's export name, display name, type and source file" };
            if (script.Length > 0) lines.Add($"{script} — reads {name}.csv with the right types and missing values");
            if (cleaning != null) lines.Add($"{files.First(f => f.Contains(" clean.", StringComparison.Ordinal))} — rebuilds {name}.csv from your ORIGINAL files, with every cleaning step written out and labelled");
            lines.AddRange(["", "Extract this folder. In R, set it as the working directory and source the import script. In SAS, set the folder macro variable in the import program. Both work offline.",
                "The cleaning script reads the original files from where they were imported; edit its folder line if they have moved.", "",
                "Missing values use the selected software's convention. SAS treats blank text as missing. Keep long identifiers or exact high-precision values as Text.",
                "Only this explicit export writes files. Original source files are unchanged."]);
            Entry(files[^1], w => w.Write(string.Join("\n", lines) + "\n"));
            return entries;
        }
        Entry("README.txt", w => w.WriteLine("WideLong " + CodeModel.Version + " export\n\nExtract this folder, set it as the working directory in R and source import.R, or set folder in import.sas. Both scripts work offline. For a wide/long ZIP, run each subfolder separately.\n\nWide: one row per ID, with optional role/entity and repeated observations in separate columns. Long: one row per ID + any configured additional key + time/grouping value. Sorting, column names and order use the saved preferences for that layout; the current text filter is applied to each exported layout.\n\nCSV headers use unique SAS-compatible names; dictionary.csv maps display names, types and sources. Missing values use the selected software convention. R-target ZIP readers preserve literal NA text, genuine empty strings and missingness. SAS-target exports follow SAS character blank conventions. SAS treats character blanks as missing. SAS cannot distinguish a genuine empty character string from character missing. Numeric/date/Boolean missing in SAS is .; in R it is typed NA. Keep long identifiers or exact high-precision values as Text.\n\nOnly this explicit export writes files. Original source files are unchanged. No session or transformation recipe is included."));
        return entries;
    }

    private static void ValidateBundle(View view, string token)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(token) > 32767 || view.Rows.Any(row => row.Any(c => System.Text.Encoding.UTF8.GetByteCount(c.Value) > 32767)))
            throw new InvalidOperationException("SAS companion scripts support at most 32,767 UTF-8 bytes per value. Use CSV-only export for this dataset.");
        if (view.Rows.Any(row => row.Any(c => c.Value.Contains('\n') || c.Value.Contains('\r'))))
            throw new InvalidOperationException("CSV export supports embedded line breaks. The SAS companion script does not yet support multiline values; choose CSV-only export.");
    }

    public static string RScript(View view, ExportTarget target = ExportTarget.R, string? token = null, string dataFile = "data.csv")
    {
        token ??= MissingToken(view, target, true);
        string file = Recipe.RQuote(dataFile);
        var b = new StringBuilder($$"""
            # Run in the extracted export folder. No packages or network required.
            # Decode without relying on the computer's current native locale.
            bytes <- readBin({{file}}, "raw", n=file.info({{file}})$size)
            text <- iconv(list(bytes), from="UTF-8", to="UTF-8")
            if (is.na(text)) stop("Cannot decode exported CSV as UTF-8")
            # Remove the record terminator; text= otherwise adds a blank record.
            text <- sub("\\r?\\n$|\\r$", "", text)
            data <- read.csv(text=text, colClasses="character", check.names=FALSE,
                             na.strings=character(), comment.char="", encoding="UTF-8",
                             blank.lines.skip=FALSE)

            """);
        for (int i = 0; i < view.Fields.Count; i++)
        {
            string column = $"data[[{i + 1}]]";
            string missing = target == ExportTarget.Sas && view.Fields[i].Kind == ValueKind.Text ? "" : token;
            b.AppendLine($"{column}[{column} == {Recipe.RQuote(missing)}] <- NA_character_");
            if (view.Fields[i].Kind == ValueKind.Text) continue;
            b.AppendLine(view.Fields[i].Kind switch
            {
                ValueKind.Number => $"{column} <- as.numeric({column})",
                ValueKind.Date => $"{column} <- as.Date({column}, format = \"%Y-%m-%d\")",
                ValueKind.Boolean => $"{column} <- ifelse(is.na({column}), NA, {column} == \"true\")",
                _ => ""
            });
        }
        for (int i = 0; i < view.Fields.Count; i++)
        {
            if (!HasLabels(view.Fields[i])) continue;
            string column = $"data[[{i + 1}]]"; var labels = OrderedLabels(view.Fields[i]);
            bool covered = view.Rows.All(r => r[i].Missing || view.Fields[i].Labels!.ContainsKey(r[i].Value));
            b.AppendLine($"# Value labels for {view.Fields[i].Name}: {DescribeLabels(view.Fields[i]).Replace("\n", " ")}");
            if (covered) b.AppendLine($"{column} <- factor({column}, levels = c({string.Join(", ", labels.Select(p => view.Fields[i].Kind == ValueKind.Number ? p.Key : Recipe.RQuote(p.Key)))}), labels = c({string.Join(", ", labels.Select(p => Recipe.RQuote(p.Value)))}))");
            else b.AppendLine($"attr({column}, \"labels\") <- {RLabelVector(view.Fields[i])}  # some values have no label, so the values are kept");
        }
        b.AppendLine("# data is ready; nothing is saved automatically."); return b.ToString();
    }

    private static string SasScript(View view, List<string> names, int[] lengths, long lrecl, ExportTarget target, string token, string dataFile = "data.csv")
    {
        var (formatCode, formats) = SasFormats(view.Fields);
        var b = new StringBuilder("/* Set folder to the extracted export directory. No network required. */\n%let folder = C:\\path\\to\\export;\n" + formatCode + "filename dhcsv \"&folder.\\" + dataFile.Replace("%", "%%") + "\" encoding='utf-8';\ndata work.prepared;\n");
        for (int i = 0; i < names.Count; i++)
            b.AppendLine($"  length {names[i]} {(view.Fields[i].Kind == ValueKind.Text ? "$" + lengths[i] : "8")};");
        // Generated temporary names must not collide with requested output names.
        var temps = new List<string>();
        var used = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Count; i++)
        {
            string temp = "_dh_raw_" + i;
            while (!used.Add(temp)) temp += "_";
            temps.Add(temp); b.AppendLine($"  length {temp} ${lengths[i]};");
        }
        b.AppendLine($"  infile dhcsv dsd dlm=',' firstobs=2 truncover lrecl={lrecl};");
        b.AppendLine("  input " + string.Join(" ", temps.Select((t, i) => t + " :$char" + lengths[i] + ".")) + ";");
        for (int i = 0; i < names.Count; i++)
        {
            string n = names[i], raw = temps[i];
            b.AppendLine(view.Fields[i].Kind switch
            {
                ValueKind.Text => $"  if {raw} = {Recipe.SasQuote(target == ExportTarget.Sas ? "" : token)} then {n} = ''; else {n} = {raw};",
                ValueKind.Number => $"  if {raw}={Recipe.SasQuote(token)} then {n}=.; else {n} = input({raw}, best32.);",
                ValueKind.Date => $"  if {raw}={Recipe.SasQuote(token)} then {n}=.; else {n} = input({raw}, yymmdd10.); format {n} yymmdd10.;",
                ValueKind.Boolean => $"  if {raw} = 'true' then {n} = 1; else if {raw} = 'false' then {n} = 0; else {n} = .;",
                _ => ""
            });
        }
        if (formats.Count > 0) b.AppendLine("  format " + string.Join(" ", formats.Select(p => $"{names[p.Key]} {p.Value}.")) + ";  /* value labels */");
        b.AppendLine("  drop " + string.Join(" ", temps) + ";\nrun;\nfilename dhcsv clear;");
        return b.ToString();
    }
}
