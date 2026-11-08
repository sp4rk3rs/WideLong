using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WideLong;

public partial class MainWindow
{
    private static string PreviewValue(string value) => value.Length == 0 ? "Empty text" : "“" + (value.Length > 100 ? value[..100] + "…" : value).Replace("\r", "\\r").Replace("\n", "\\n") + "”";

    private void MissingRules(Source source, int column, bool repeatGroup = false)
    {
        if (busy) return;
        var field = source.Fields[column];
        var d = new FormDialog(this, "Missing-value rules · " + field.Name, "Rules affect only the chosen variable or repeated-column family. Values in other variables keep their own rules. Originals remain unchanged.") { Width = 630 };
        var mode = d.Choice("Rules to use", ["Use this file's default rules", "Use custom rules for this variable"], field.MissingTokens == null ? 0 : 1);
        var controls = MissingControls(d, new HashSet<string>(source.EffectiveMissingTokens(column), StringComparer.Ordinal));
        var group = new CheckBox { Content = "Apply to every repeated column of " + field.Measure, IsChecked = repeatGroup, Visibility = field.Measure.Length > 0 ? Visibility.Visible : Visibility.Collapsed, Margin = new(0, 7, 0, 8) }; d.Add(group);
        var summary = new TextBlock { FontSize = 12, Margin = new(0, 9, 0, 0) }; d.Add(summary);
        List<int> Columns() => group.IsChecked == true && field.Measure.Length > 0 ? source.Fields.Select((f, i) => (f, i)).Where(x => x.f.Measure == field.Measure).Select(x => x.i).ToList() : [column];
        Source Proposed()
        {
            var copy = source.Copy();
            foreach (int c in Columns()) copy.Fields[c].MissingTokens = mode.SelectedIndex == 0 ? null : ReadMissing(controls);
            return copy;
        }
        void Update()
        {
            controls.Blank.IsEnabled = controls.Tokens.IsEnabled = mode.SelectedIndex == 1;
            var copy = Proposed(); var columns = Columns();
            int total = source.Rows.Count * columns.Count, before = 0, after = 0, newlyMissing = 0, newlyVisible = 0;
            foreach (int c in columns) foreach (var row in source.Rows)
            {
                bool oldMissing = source.IsMissing(row[c], c), newMissing = copy.IsMissing(row[c], c);
                if (oldMissing) before++; if (newMissing) after++;
                if (!oldMissing && newMissing) newlyMissing++; if (oldMissing && !newMissing) newlyVisible++;
            }
            var invalid = Values.ValidatePolicies(copy).Where(x => columns.Contains(x.Column)).ToList();
            summary.Text = $"{total:N0} values · missing: {before:N0} → {after:N0}\n{newlyMissing:N0} newly missing · {newlyVisible:N0} become visible again. No raw text values are rewritten." + (invalid.Count > 0 ? $"\n{invalid.Count:N0} values would be invalid for the current variable type. Keep their missing rule, recode them, or change the type to Text first.\n" + string.Join("\n", invalid.Take(3).Select(x => source.Fields[x.Column].Name + ", record " + x.Row + ": " + PreviewValue(x.Value))) : "");
            summary.Foreground = invalid.Count == 0 ? (Brush)FindResource("Accent") : Brushes.DarkRed;
        }
        mode.SelectionChanged += (_, _) => Update(); controls.Blank.Checked += (_, _) => Update(); controls.Blank.Unchecked += (_, _) => Update(); controls.Tokens.TextChanged += (_, _) => Update(); group.Checked += (_, _) => Update(); group.Unchecked += (_, _) => Update(); Update();
        d.Description("Choose the file defaults to reset a variable's override. Numeric, Date and Boolean blanks are always missing; genuine empty Text can stay visible.");
        if (d.Finish("Apply rules") != true) return;
        var proposed = Proposed(); var selected = Columns();
        if (Values.ValidatePolicies(proposed).Any(x => selected.Contains(x.Column))) { MessageBox.Show(this, "These rules would expose values that do not match the current type. Resolve those values or use Text first. No changes were applied.", "Missing-value rules", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Change(() => { foreach (int c in selected) source.Fields[c].MissingTokens = proposed.Fields[c].MissingTokens; }, "Updated missing-value rules for " + source.Name + " / " + (selected.Count == 1 ? field.Name : field.Measure + " (" + selected.Count + " columns)"));
    }
}
