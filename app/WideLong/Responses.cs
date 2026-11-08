using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WideLong;

public partial class MainWindow
{
    /// <summary>After an import or example, offers to treat mostly-numeric Text variables as numbers (one yes/no prompt).</summary>
    private void OfferNumericTypes(IEnumerable<Source> sources) => Dispatcher.BeginInvoke(() =>
    {
        if (busy) { OfferNumericTypes(sources); return; }
        var scope = sources.Where(session.Sources.Contains).ToList();
        if (scope.Count == 0) return;
        var found = NumericDetection.Find(session, scope);
        if (found.Count == 0) return;
        var d = new FormDialog(this, "Treat these as numbers?", $"{found.Count:N0} variable{(found.Count == 1 ? " is" : "s are")} stored as text but contain{(found.Count == 1 ? "s" : "")} only or almost only numbers. Converting lets them sort, filter and export as numbers. A few non-number codes (listed) become missing values.") { Width = 640 };
        var list = new StackPanel();
        foreach (var c in found)
        {
            string detail = c.Codes.Length == 0 ? $"all {c.Values:N0} values are numbers" : $"{c.Numbers:N0} of {c.Values:N0} values are numbers · {string.Join(", ", c.Codes.Select(PreviewValue))} → missing";
            var text = new StackPanel(); text.Children.Add(new TextBlock { Text = c.Source.Name + " / " + c.Source.Fields[c.Column].Name + (c.Source.IsKeyColumn(c.Column) ? "  · matching key" : ""), FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = (Brush)FindResource("Muted"), Margin = new(0, 2, 0, 6), TextWrapping = TextWrapping.Wrap });
            list.Children.Add(text);
        }
        d.Add(new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        d.Description("You can change your mind later: right-click a variable → Revert variable type.");
        if (d.Finish("Yes, convert to numbers", cancelText: "No, keep as text") != true) { Status.Text = "Kept those variables as text · right-click → Change variable type to convert one later"; return; }
        Change(() => NumericDetection.Apply(found), $"Converted {found.Count:N0} mostly-numeric variable{(found.Count == 1 ? "" : "s")} to Number");
    }, DispatcherPriority.Background);

    private static bool HasTypeChange(Source source, int column) => source.Steps.Any(st => st.Kind == StepKind.Convert && st.Column == column);
    private List<(Source Source, int Column)> RevertTargets(Source source, int column)
    {
        string? key = KeyFor(source, column);
        var targets = key == null ? [(source, column)] : KeyTargets(key);
        return targets.Where(t => HasTypeChange(t.Source, t.Column)).ToList();
    }
    private void RevertType(Source source, int column)
    {
        if (busy) return;
        var targets = RevertTargets(source, column);
        if (targets.Count == 0) { MessageBox.Show(this, "This variable has no recorded type change to revert."); return; }
        try
        {
            var trial = session.Copy();
            foreach (var (s, c) in targets) NumericDetection.Revert(trial.Sources[session.Sources.IndexOf(s)], c);
        }
        catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message, "Revert variable type", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Change(() =>
        {
            foreach (var (s, c) in targets)
            {
                // A response filter keeps the same records after the revert: its values become those records' original text.
                var rule = s.KeepRules.FirstOrDefault(r => r.Column == c);
                var kept = rule == null ? [] : Enumerable.Range(0, s.Rows.Count).Where(r => s.IsMissing(s.Rows[r][c], c) ? rule.KeepMissing : rule.Values.Contains(s.Rows[r][c], StringComparer.Ordinal)).ToList();
                NumericDetection.Revert(s, c);
                if (rule != null) s.KeepRules[s.KeepRules.IndexOf(rule)] = rule with { Values = kept.Where(r => !s.IsMissing(s.Rows[r][c], c)).Select(r => s.Rows[r][c]).Distinct(StringComparer.Ordinal).ToArray() };
            }
        },
            "Reverted " + (targets.Count == 1 ? targets[0].Source.Name + " / " + targets[0].Source.Fields[targets[0].Column].Name : KeyRole(KeyFor(source, column) ?? "") + " in " + targets.Count + " files") + " to its original text");
    }
    /// <summary>A type change rewrites stored values, so response filters on that variable follow the new text (or are cleared on revert).</summary>
    private static void RemapKeepRule(Source source, int column, Func<string, string> convert, bool keepValues = true)
    {
        int index = source.KeepRules.FindIndex(r => r.Column == column);
        if (index < 0) return;
        if (!keepValues) { source.KeepRules.RemoveAt(index); return; }
        var rule = source.KeepRules[index]; var mapped = new List<string>();
        foreach (string v in rule.Values) { try { mapped.Add(convert(v)); } catch (InvalidOperationException) { } }
        source.KeepRules[index] = rule with { Values = mapped.Distinct(StringComparer.Ordinal).ToArray() };
    }

    /// <summary>Which source variables a response filter on this output column changes; null with a reason when unavailable.</summary>
    private (List<(Source Source, int Column)>? Targets, string Why) ResponseTargets(OutputField field)
    {
        if (field.Key is "@person_id" or "@entity" or "@time")
        {
            var keys = KeyTargets(field.Key);
            return keys.Count == 0 ? (null, "No file has this matching column.") : (keys, "");
        }
        if (field.Key.StartsWith('@')) return (null, "This column is generated by the layout and has no source values to filter.");
        if (ResolveField(field) is not { } x) return (null, "This column has no single source variable.");
        return ResponseTargets(x.Source, x.Column);
    }
    private (List<(Source Source, int Column)>? Targets, string Why) ResponseTargets(Source source, int column)
    {
        if (KeyFor(source, column) is { } key) return (KeyTargets(key), "");
        if (source.Fields[column].Measure.Length > 0) return (null, "Repeated columns hold one value per time, so a response filter would drop whole records. Use Filter rules instead.");
        return ([(source, column)], "");
    }

    private void FilterResponses(List<(Source Source, int Column)> targets)
    {
        if (busy || targets.Count == 0) return;
        var first = targets[0]; var field = first.Source.Fields[first.Column];
        bool numeric = targets.All(t => t.Source.Fields[t.Column].Kind == ValueKind.Number);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal); int missing = 0;
        foreach (var (s, c) in targets) foreach (var row in s.Rows)
            if (s.IsMissing(row[c], c)) missing++; else counts[row[c]] = counts.GetValueOrDefault(row[c]) + 1;
        const int Limit = 400;
        if (counts.Count > Limit) { MessageBox.Show(this, $"{field.Name} has {counts.Count:N0} different responses. Use Filter rules (equals, between, contains…) for variables with this many values.", "Filter responses", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        var values = counts.Keys.OrderBy(v => numeric && decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0m).ThenBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        var rules = targets.Select(t => t.Source.KeepRules.FirstOrDefault(r => r.Column == t.Column)).ToList();
        bool Kept(string value) => rules.All(r => r == null || r.Values.Contains(value, StringComparer.Ordinal));
        bool keptMissing = rules.All(r => r == null || r.KeepMissing);
        string where = targets.Count == 1 ? first.Source.Name : $"every file with this {KeyRole(KeyFor(first.Source, first.Column) ?? "")} column ({targets.Count} files)";
        var d = new FormDialog(this, "Filter responses · " + field.Name, $"Checked responses stay in the dataset. Records with an unchecked response are removed from {where} before files are combined, so every layout, export and cleaning script leaves them out. Files without this variable are not affected.") { Width = 560 };
        var commands = new WrapPanel { Margin = new(0, 2, 0, 8) }; var all = SmallButton("Select all"); var none = SmallButton("Clear"); commands.Children.Add(all); commands.Children.Add(none); d.Add(commands);
        var list = new StackPanel(); var checks = new List<(string? Value, CheckBox Box)>();
        foreach (string v in values) { var box = new CheckBox { Content = (v.Length == 0 ? "(empty text)" : v) + $"   ·  {counts[v]:N0} record{(counts[v] == 1 ? "" : "s")}", IsChecked = Kept(v), Margin = new(0, 4, 0, 4) }; checks.Add((v, box)); list.Children.Add(box); }
        if (missing > 0) { var box = new CheckBox { Content = $"(missing)   ·  {missing:N0} record{(missing == 1 ? "" : "s")}", IsChecked = keptMissing, Margin = new(0, 4, 0, 4), FontStyle = FontStyles.Italic }; checks.Add((null, box)); list.Children.Add(box); }
        d.Add(new ScrollViewer { Content = list, MaxHeight = 340, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var summary = new TextBlock { FontSize = 12, Margin = new(0, 10, 0, 0) }; d.Add(summary);
        void Update()
        {
            int removed = checks.Where(c => c.Box.IsChecked != true).Sum(c => c.Value == null ? missing : counts[c.Value]);
            summary.Text = removed == 0 ? "All responses kept (no filter)." : $"{removed:N0} records will be left out.";
            summary.Foreground = (Brush)FindResource("Accent");
        }
        foreach (var c in checks) { c.Box.Checked += (_, _) => Update(); c.Box.Unchecked += (_, _) => Update(); }
        all.Click += (_, _) => { foreach (var c in checks) c.Box.IsChecked = true; }; none.Click += (_, _) => { foreach (var c in checks) c.Box.IsChecked = false; };
        Update();
        if (d.Finish("Apply") != true) return;
        var keep = checks.Where(c => c.Box.IsChecked == true && c.Value != null).Select(c => c.Value!).ToArray();
        bool keepMissing = missing == 0 || checks.Any(c => c.Value == null && c.Box.IsChecked == true);
        if (keep.Length == 0 && !keepMissing) { MessageBox.Show(this, "Keep at least one response. No changes were applied.", "Filter responses"); return; }
        bool everything = keep.Length == values.Count && keepMissing;
        var shown = checks.Where(c => c.Box.IsChecked != true).Select(c => c.Value == null ? "(missing)" : c.Value.Length == 0 ? "(empty text)" : c.Value).ToList();
        Change(() =>
        {
            foreach (var (s, c) in targets)
            {
                s.KeepRules.RemoveAll(r => r.Column == c);
                if (!everything) s.KeepRules.Add(new(c, keep, keepMissing));
            }
        }, everything ? "Removed the response filter on " + field.Name : $"Response filter on {field.Name}: left out " + string.Join(", ", shown.Take(5)) + (shown.Count > 5 ? ", …" : ""));
    }
}
