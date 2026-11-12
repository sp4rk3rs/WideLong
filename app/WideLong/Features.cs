using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WideLong;

/// <summary>0.6.0 tools: saved recipes, value labels and scale scores.</summary>
public partial class MainWindow
{
    // ---- Recipes ----
    private void Recipe_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(Menu("Save recipe…", SaveRecipe, session.Sources.Count > 0, "Import files or open an example first."));
        menu.Items.Add(Menu("Open recipe…", OpenRecipe));
        menu.IsOpen = true;
    }

    private void SaveRecipe()
    {
        if (busy || session.Sources.Count == 0) return;
        var d = new FormDialog(this, "Save recipe", "A recipe stores how to rebuild this session from your original files: ID / grouping choices, kept and renamed variables, types, missing-value rules, mappings, filters, corrections, scales, value labels, layouts, sorting and column names.");
        d.Description("It contains no data rows. Single-value corrections and recodes include the values you typed, and imported files are listed with their folder paths, like the cleaning code. Review it before sharing.");
        if (d.Finish("Choose where to save…") != true) return;
        var picker = new SaveFileDialog { Title = "Save WideLong recipe", FileName = (session.Sources.Count == 1 ? Path.GetFileNameWithoutExtension(session.Sources[0].Name) : "widelong") + SessionRecipes.Extension, Filter = "WideLong recipe|*" + SessionRecipes.Extension + "|JSON|*.json", AddExtension = true, OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        if (session.Sources.Any(s => s.ImportPath != null && string.Equals(Path.GetFullPath(picker.FileName), s.ImportPath, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(this, "Choose a different filename. Imported originals cannot be overwritten."); return; }
        try
        {
            var recipe = SessionRecipes.Capture(session, layout, (MatchMode)MatchRule.SelectedIndex, CombineMode.SelectedIndex == 1, selectedSource?.Key, preferences, RowFilter.Text, structuredFilter);
            File.WriteAllText(picker.FileName, SessionRecipes.Serialize(recipe), new UTF8Encoding(false));
            Status.Text = "Saved recipe " + Path.GetFileName(picker.FileName) + " · no data rows were written"; RecordActivity("Export", Status.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { MessageBox.Show(this, ex.Message, "Save recipe", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void OpenRecipe()
    {
        if (busy) return;
        if (Title.Contains("synthetic verification", StringComparison.Ordinal)) { MessageBox.Show(this, "This verification window uses only fictional data. Use the normal application to open recipes."); return; }
        var picker = new OpenFileDialog { Title = "Open WideLong recipe", Filter = "WideLong recipe|*" + SessionRecipes.Extension + ";*.json|All files|*.*" };
        if (picker.ShowDialog(this) != true) return;
        RecipeDocument recipe;
        try { recipe = SessionRecipes.Parse(File.ReadAllText(picker.FileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { MessageBox.Show(this, ex.Message, "Open recipe", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (session.Sources.Count > 0 && MessageBox.Show(this, "Replace this session with the recipe applied to its original files? You can undo the replacement.", "Open recipe", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        string recipeFolder = Path.GetDirectoryName(picker.FileName) ?? "";
        (string Text, string? Path) Locate(RecipeFile file)
        {
            // The recorded path first, then the recipe's own folder, then ask.
            foreach (var candidate in new[] { file.Path, Path.Combine(recipeFolder, file.Path == null ? file.Name : Path.GetFileName(file.Path)) })
                if (candidate != null && File.Exists(candidate)) return (SessionRecipes.ReadText(candidate, file.Encoding), Path.GetFullPath(candidate));
            var find = new OpenFileDialog { Title = "Locate " + (file.Path == null ? file.Name : Path.GetFileName(file.Path)) + " (original file for this recipe)", Filter = "CSV / delimited files|*.csv;*.tsv;*.txt|All files|*.*" };
            if (find.ShowDialog(this) != true) throw new OperationCanceledException();
            return (SessionRecipes.ReadText(find.FileName, file.Encoding), Path.GetFullPath(find.FileName));
        }
        try
        {
            var (opened, notes) = SessionRecipes.Open(recipe, Locate);
            Change(() =>
            {
                session = opened; selectedSource = session.Sources.FirstOrDefault(s => s.Key == recipe.SelectedFile) ?? session.Sources[0];
                preferences.Clear(); foreach (var (key, value) in recipe.Preferences) preferences[key] = value.Copy();
                structuredFilter = recipe.Filter.Copy(); RowFilter.Text = recipe.Search; lastSearch = recipe.Search; VariableSearch.Clear();
                CombineMode.SelectedIndex = recipe.Stack ? 1 : 0; MatchRule.SelectedIndex = (int)recipe.Match; ExampleLabel.Visibility = Visibility.Collapsed;
                layout = recipe.Layout;
                foreach (var radio in new[] { IdMode, TimeMode, SourceMode, DyadMode }) if (radio.Tag?.ToString() == recipe.Layout.ToString()) radio.IsChecked = true;
            }, $"Opened recipe {Path.GetFileName(picker.FileName)} · {opened.Sources.Count} file(s) rebuilt from the originals" + (notes.Count > 0 ? " · " + notes[0] : ""));
            if (notes.Count > 0) MessageBox.Show(this, string.Join("\n", notes) + "\n\nThe recorded steps were reapplied to the current files.", "Open recipe", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) { Status.Text = "Recipe not opened · nothing changed"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DecoderFallbackException) { MessageBox.Show(this, ex.Message + "\n\nNothing changed.", "Open recipe", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    // ---- Value labels ----
    private void ValueLabels(Source source, int column)
    {
        if (busy) return;
        var field = source.Fields[column];
        if (field.Kind is not (ValueKind.Number or ValueKind.Text)) { MessageBox.Show(this, "Value labels are available for Text and Number variables.", "Value labels"); return; }
        var d = new FormDialog(this, "Value labels · " + field.Name, "One label per line, for example 1 = Strongly disagree. Labels do not change the values. Exports add them to the dictionary, as R factors or labels, and as SAS formats.");
        var box = new TextBox { AcceptsReturn = true, MinHeight = 140, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Text = string.Join("\n", Export.OrderedLabels(new OutputField(field.Name, field.Kind, source.Name) { Labels = field.ValueLabels }).Select(p => p.Key + " = " + p.Value)) };
        if (box.Text.Length == 0)
        {
            // Offer the values that occur, so the user only types the labels.
            var present = source.Rows.Select(r => r[column]).Where(v => !source.IsMissing(v, column)).Distinct(StringComparer.Ordinal).ToList();
            if (present.Count <= 12) box.Text = string.Join("\n", (field.Kind == ValueKind.Number ? present.OrderBy(v => decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0m) : present.Order(StringComparer.Ordinal)).Select(v => v + " = "));
        }
        d.Add(box); var problem = new TextBlock { Foreground = Brushes.DarkRed, FontSize = 12, Margin = new(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap }; d.Add(problem);
        d.Description("Leave the box empty to remove the labels.");
        if (d.Finish("Apply labels") != true) return;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in box.Text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0))
        {
            int at = line.IndexOf('=');
            string value = at < 0 ? "" : line[..at].Trim(), label = at < 0 ? "" : line[(at + 1)..].Trim();
            if (value.Length == 0 || label.Length == 0) { MessageBox.Show(this, $"Write each label as value = label (\"{line.Trim()}\"). Nothing changed.", "Value labels"); return; }
            if (field.Kind == ValueKind.Number) { try { value = Values.Convert(value, ValueKind.Number); } catch (InvalidOperationException) { MessageBox.Show(this, $"{value} is not a number. Nothing changed.", "Value labels"); return; } }
            if (!labels.TryAdd(value, label)) { MessageBox.Show(this, $"{value} is labelled twice. Nothing changed.", "Value labels"); return; }
        }
        if (labels.Values.Distinct(StringComparer.Ordinal).Count() != labels.Count) { MessageBox.Show(this, "Two values share a label; R factors need distinct labels. Nothing changed.", "Value labels"); return; }
        Change(() => field.ValueLabels = labels.Count == 0 ? null : labels, labels.Count == 0 ? "Removed value labels from " + field.Name : $"Labelled {labels.Count} values of {source.Name} / {field.Name}");
    }

    // ---- Scale scores ----
    private void ScaleDialog(Source source, ScaleScore? editing = null)
    {
        if (busy) return;
        var candidates = Enumerable.Range(0, source.FileColumns).Where(c => !source.IsKeyColumn(c) && source.Fields[c].Measure.Length == 0).ToList();
        if (candidates.Count(c => source.Fields[c].Kind == ValueKind.Number) < 2) { MessageBox.Show(this, $"A scale needs at least two Number variables in {source.Name}. Right-click item variables → Change variable type → Number first.", "Scale score"); return; }
        var d = new FormDialog(this, editing == null ? "New scale score · " + source.Name : "Edit scale · " + editing.Name, "Reverse-code items, then compute the mean or sum of the items for each record. The score is added as a new Number variable and is repeated in the R and SAS cleaning code.") { Width = 620 };
        var name = d.Input("Scale name", editing?.Name ?? "scale_score");
        var method = d.Choice("Score", ["Mean of the answered items", "Sum of the answered items"], (int)(editing?.Method ?? ScaleMethod.Mean));
        d.Description("Check the items. Tick R for items that are reverse-coded.");
        var rows = new List<(int Column, CheckBox Item, CheckBox Reverse)>(); var list = new StackPanel();
        foreach (int c in candidates)
        {
            var f = source.Fields[c]; bool numeric = f.Kind == ValueKind.Number;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 3, 0, 3) };
            var item = new CheckBox { Content = f.Name + (numeric ? "" : "  (Text: convert to Number first)"), IsEnabled = numeric, IsChecked = editing?.Items.Contains(f.Original) == true, Width = 360 };
            var reverse = new CheckBox { Content = "R", ToolTip = "Reverse-code this item", IsEnabled = numeric, IsChecked = editing?.Reversed.Contains(f.Original) == true };
            row.Children.Add(item); row.Children.Add(reverse); list.Children.Add(row); rows.Add((c, item, reverse));
        }
        d.Add(new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var low = d.Input("Lowest possible answer (for reverse coding)", (editing?.ReverseMin ?? 1).ToString(CultureInfo.InvariantCulture));
        var high = d.Input("Highest possible answer", (editing?.ReverseMax ?? 5).ToString(CultureInfo.InvariantCulture));
        var minimum = d.Input("Score a record when at least this many items are answered", (editing?.MinItems ?? 1).ToString(CultureInfo.InvariantCulture));
        d.Description("A reverse-coded answer x becomes lowest + highest − x (on a 1–5 scale, 1 ↔ 5 and 2 ↔ 4). Records with fewer answered items get a missing score.");
        var remove = new CheckBox { Content = "Remove this scale", Margin = new(0, 10, 0, 0), Visibility = editing == null ? Visibility.Collapsed : Visibility.Visible }; d.Add(remove);
        if (d.Finish(editing == null ? "Add scale" : "Apply") != true) return;
        if (editing != null && remove.IsChecked == true) { Change(() => Scales.Remove(source, editing), "Removed scale " + editing.Name); return; }
        if (!decimal.TryParse(low.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var lo) || !decimal.TryParse(high.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hi) || !int.TryParse(minimum.Text, out int min))
        { MessageBox.Show(this, "Enter numbers for the answer range and the minimum number of items. Nothing changed.", "Scale score"); return; }
        var scale = new ScaleScore
        {
            Name = name.Text.Trim(), Method = (ScaleMethod)method.SelectedIndex, ReverseMin = lo, ReverseMax = hi, MinItems = min,
            Items = rows.Where(r => r.Item.IsChecked == true).Select(r => source.Fields[r.Column].Original).ToList(),
            Reversed = rows.Where(r => r.Item.IsChecked == true && r.Reverse.IsChecked == true).Select(r => source.Fields[r.Column].Original).ToList()
        };
        if (Scales.Problem(source, scale, editing) is { } problem) { MessageBox.Show(this, problem + " Nothing changed.", "Scale score"); return; }
        Change(() => Scales.Add(source, scale, editing), (editing == null ? "Added scale " : "Updated scale ") + scale.Name + " · " + scale.Describe());
    }
    private ScaleScore? ScaleAt(Source source, int column) => source.IsDerived(column) ? source.Scales[column - source.FileColumns] : null;
    private ContextMenu DerivedMenu(Source source, int column)
    {
        var menu = new ContextMenu(); var scale = ScaleAt(source, column)!;
        menu.Items.Add(Menu("Edit scale…", () => ScaleDialog(source, scale)));
        menu.Items.Add(Menu("Rename source column…", () => RenameSource(source, column)));
        menu.Items.Add(Menu("Value labels…", () => ValueLabels(source, column)));
        menu.Items.Add(Menu("Remove scale", () => Change(() => Scales.Remove(source, scale), "Removed scale " + scale.Name)));
        menu.Items.Add(new Separator()); menu.Items.Add(Menu("New scale score…", () => ScaleDialog(source)));
        return menu;
    }
}
