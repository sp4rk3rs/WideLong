using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WideLong;

/// <summary>One variable's settings in a saved recipe.</summary>
public sealed class RecipeField
{
    public string Original { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Keep { get; set; } = true;
    public ValueKind Kind { get; set; }
    public string Measure { get; set; } = "";
    public string Occasion { get; set; } = "";
    public string[]? MissingTokens { get; set; }
    public Dictionary<string, string>? ValueLabels { get; set; }
}

/// <summary>One original file in a saved recipe: how to read it, its keys, variable settings and recorded cleaning steps. No data rows.</summary>
public sealed class RecipeFile
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Where the file was imported from, so reopening can find it again. Null for built-in examples.</summary>
    public string? Path { get; set; }
    public string[] Headers { get; set; } = [];
    public int Rows { get; set; }
    public string Delimiter { get; set; } = ",";
    public string Encoding { get; set; } = "UTF-8";
    public int SkipRows { get; set; }
    public string[] MissingTokens { get; set; } = [""];
    public int IdColumn { get; set; } = -1;
    public int EntityColumn { get; set; } = -1;
    public int TimeColumn { get; set; } = -1;
    public List<RecipeField> Fields { get; set; } = [];
    public List<CleaningStep> Steps { get; set; } = [];
    public List<KeepRule> KeepRules { get; set; } = [];
    public List<ScaleScore> Scales { get; set; } = [];
}

/// <summary>A saved WideLong recipe: every setting and recorded step needed to rebuild a session from the original files.
/// It never contains data rows. Values typed into single-value corrections and recodes are included, like in the cleaning code.</summary>
public sealed class RecipeDocument
{
    public const string FormatName = "WideLong recipe";
    public string Format { get; set; } = FormatName;
    public int FormatVersion { get; set; } = 1;
    public string App { get; set; } = "WideLong " + CodeModel.Version;
    public List<RecipeFile> Files { get; set; } = [];
    public List<string> TimeOrder { get; set; } = [];
    public Layout Layout { get; set; } = Layout.PersonTime;
    public MatchMode Match { get; set; }
    public bool Stack { get; set; }
    public string? SelectedFile { get; set; }
    /// <summary>Sorting, column order, names and custom rows for each layout (keyed like the application's own preferences).</summary>
    public Dictionary<string, ViewPreferences> Preferences { get; set; } = new(StringComparer.Ordinal);
    public string Search { get; set; } = "";
    public RowFilterSpec Filter { get; set; } = new();
}

public static class SessionRecipes
{
    public const string Extension = ".widelong.json";
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() }, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static RecipeDocument Capture(Session session, Layout layout, MatchMode match, bool stack, string? selectedFile, IReadOnlyDictionary<string, ViewPreferences> preferences, string search, RowFilterSpec filter)
    {
        if (session.Sources.Count == 0) throw new InvalidOperationException("Import files or choose an example first.");
        return new()
        {
            Files = session.Sources.Select(s => new RecipeFile
            {
                Key = s.Key, Name = s.Name, Path = s.ImportPath, Headers = s.Fields.Take(s.FileColumns).Select(f => f.Original).ToArray(), Rows = s.OriginalRows?.Count ?? s.Rows.Count,
                Delimiter = s.Delimiter.ToString(), Encoding = s.EncodingName, SkipRows = s.SkipRows, MissingTokens = s.MissingTokens.Order(StringComparer.Ordinal).ToArray(),
                IdColumn = s.IdColumn, EntityColumn = s.EntityColumn, TimeColumn = s.TimeColumn,
                Fields = s.Fields.Select(f => new RecipeField { Original = f.Original, Name = f.Name, Keep = f.Keep, Kind = f.Kind, Measure = f.Measure, Occasion = f.Occasion, MissingTokens = f.MissingTokens?.Order(StringComparer.Ordinal).ToArray(), ValueLabels = f.ValueLabels is { Count: > 0 } labels ? new(labels, StringComparer.Ordinal) : null }).ToList(),
                Steps = [.. s.Steps], KeepRules = [.. s.KeepRules], Scales = s.Scales.Select(x => x.Copy()).ToList()
            }).ToList(),
            TimeOrder = [.. session.TimeOrder], Layout = layout, Match = match, Stack = stack, SelectedFile = selectedFile,
            Preferences = preferences.ToDictionary(p => p.Key, p => p.Value.Copy(), StringComparer.Ordinal), Search = search, Filter = filter.Copy()
        };
    }

    public static string Serialize(RecipeDocument recipe) => JsonSerializer.Serialize(recipe, Options);

    public static RecipeDocument Parse(string json)
    {
        RecipeDocument? recipe;
        try { recipe = JsonSerializer.Deserialize<RecipeDocument>(json, Options); }
        catch (JsonException ex) { throw new InvalidOperationException("This is not a readable WideLong recipe (" + ex.Message + ")."); }
        if (recipe == null || recipe.Format != RecipeDocument.FormatName) throw new InvalidOperationException("This file is not a WideLong recipe.");
        if (recipe.FormatVersion > 1) throw new InvalidOperationException("This recipe was saved by a newer WideLong. Update WideLong to open it.");
        if (recipe.Files.Count == 0) throw new InvalidOperationException("The recipe lists no files.");
        foreach (var file in recipe.Files)
            if (file.Fields.Count != file.Headers.Length + file.Scales.Count || file.Delimiter.Length != 1) throw new InvalidOperationException($"The recipe entry for {file.Name} is damaged.");
        return recipe;
    }

    /// <summary>Reads one original file and reapplies its saved settings and steps. Stops if the file's columns differ or a recorded
    /// single-value correction no longer finds the value it changed (the file was edited since the recipe was saved).</summary>
    public static Source Rebuild(RecipeFile file, string text, string? importPath)
    {
        var source = Csv.Import(text, file.Name, file.Delimiter[0], file.SkipRows);
        var headers = source.Fields.Select(f => f.Original).ToArray();
        if (!headers.SequenceEqual(file.Headers, StringComparer.Ordinal))
            throw new InvalidOperationException($"{file.Name}: the columns differ from the recipe. Expected {string.Join(", ", file.Headers.Take(6))}{(file.Headers.Length > 6 ? ", …" : "")}; found {string.Join(", ", headers.Take(6))}{(headers.Length > 6 ? ", …" : "")}.");
        source.Key = file.Key; source.ImportPath = importPath; source.EncodingName = file.Encoding;
        source.MissingTokens = new(file.MissingTokens, StringComparer.Ordinal);
        // Scale scores are derived columns after the file's own columns.
        foreach (var scale in file.Scales) source.Fields.Add(new Field { Original = scale.Name, Name = scale.Name, Kind = ValueKind.Number });
        for (int c = 0; c < source.Fields.Count; c++)
        {
            var saved = file.Fields[c]; var field = source.Fields[c];
            field.Name = saved.Name; field.Keep = saved.Keep; field.Kind = saved.Kind; field.Measure = saved.Measure; field.Occasion = saved.Occasion;
            field.MissingTokens = saved.MissingTokens == null ? null : new(saved.MissingTokens, StringComparer.Ordinal);
            field.ValueLabels = saved.ValueLabels == null ? null : new(saved.ValueLabels, StringComparer.Ordinal);
        }
        int n = source.Fields.Count;
        if (new[] { file.IdColumn, file.EntityColumn, file.TimeColumn }.Any(c => c < -1 || c >= n)) throw new InvalidOperationException($"The recipe entry for {file.Name} is damaged.");
        if (file.Steps.Any(st => st.Column < 0 || st.Column >= n) || file.KeepRules.Any(r => r.Column < 0 || r.Column >= n)) throw new InvalidOperationException($"The recipe entry for {file.Name} is damaged.");
        source.IdColumn = file.IdColumn; source.EntityColumn = file.EntityColumn; source.TimeColumn = file.TimeColumn;
        source.IdChosen = source.EntityChosen = source.GroupChosen = true;
        source.IdReason = "Saved in the recipe."; source.EntityReason = "Saved in the recipe."; source.GroupReason = "Saved in the recipe.";
        source.Steps = [.. file.Steps]; source.KeepRules = [.. file.KeepRules]; source.Scales = file.Scales.Select(x => x.Copy()).ToList();
        foreach (var scale in source.Scales) { var items = Scales.ItemColumns(source, scale); if (items.Any(c => c < 0 || c >= source.FileColumns)) throw new InvalidOperationException($"The recipe entry for {file.Name} is damaged."); }
        try { source.Rows = Recipe.Replay(source); }
        catch (InvalidOperationException) { throw new InvalidOperationException($"{file.Name}: a recorded single-value correction no longer matches the file. The file was changed after the recipe was saved."); }
        Scales.Refresh(source);
        return source;
    }

    /// <summary>Rebuilds a whole session. readFile returns the text and path of a recipe file (or throws when the user cancels).</summary>
    public static (Session Session, List<string> Notes) Open(RecipeDocument recipe, Func<RecipeFile, (string Text, string? Path)> readFile)
    {
        var session = new Session { TimeOrder = [.. recipe.TimeOrder] }; var notes = new List<string>();
        foreach (var file in recipe.Files)
        {
            var (text, path) = readFile(file);
            var source = Rebuild(file, text, path);
            if (source.Rows.Count != file.Rows) notes.Add($"{file.Name} now has {source.Rows.Count:N0} data rows ({file.Rows:N0} when the recipe was saved).");
            session.Sources.Add(source);
        }
        if (session.Sources.Select(s => s.Key).Distinct().Count() != session.Sources.Count) throw new InvalidOperationException("The recipe lists the same file key twice.");
        return (session, notes);
    }

    /// <summary>Decodes an original file with the encoding recorded for it.</summary>
    public static string ReadText(string path, string encodingName)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        System.Text.Encoding decoder = encodingName switch
        {
            "UTF-16LE" => new System.Text.UnicodeEncoding(false, true, true),
            "windows-1252" => System.Text.Encoding.GetEncoding(1252, System.Text.EncoderFallback.ExceptionFallback, System.Text.DecoderFallback.ExceptionFallback),
            _ => new System.Text.UTF8Encoding(false, true)
        };
        using var reader = new StreamReader(path, decoder, detectEncodingFromByteOrderMarks: false);
        return reader.ReadToEnd();
    }
}
