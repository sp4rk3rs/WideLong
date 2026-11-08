using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace WideLong;

public partial class MainWindow
{
    private async void MatchingReview_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session.Sources.Count == 0 || !await CommitPendingEdit()) return;
        OpenMatchingReview();
    }
    private void OpenMatchingReview()
    {
        CloseHover();
        var reports = MatchingDiagnostics.Analyze(session);
        var dialog = new FormDialog(this, "Review file matching", "Review every file's keys before matching. Counts use exact ID values. Records with missing key values are excluded from matching coverage; duplicate keys are shown separately.") { Width = 1060, MaxHeight = Math.Max(600, Math.Min(900, SystemParameters.WorkArea.Height - 40)) };
        var summary = ReviewGrid(120);
        ReviewColumn(summary, "File", nameof(FileMatchReport.File), 185);
        ReviewColumn(summary, "ID", nameof(FileMatchReport.IdColumn), 120);
        ReviewColumn(summary, "Additional key", nameof(FileMatchReport.EntityColumn), 135);
        ReviewColumn(summary, "Grouping", nameof(FileMatchReport.GroupingColumn), 130);
        ReviewColumn(summary, "Rows", nameof(FileMatchReport.Rows), 75);
        ReviewColumn(summary, "Complete IDs", nameof(FileMatchReport.DistinctIds), 95);
        ReviewColumn(summary, "Unique keys", nameof(FileMatchReport.DistinctKeys), 90);
        ReviewColumn(summary, "Missing-key rows", nameof(FileMatchReport.MissingKeyRows), 115);
        ReviewColumn(summary, "Duplicate keys", nameof(FileMatchReport.DuplicateKeys), 105);
        ReviewColumn(summary, "Duplicate rows", nameof(FileMatchReport.DuplicateRows), 105);
        summary.ItemsSource = reports; dialog.Add(summary);
        int selected = Math.Max(0, reports.FindIndex(r => r.SourceKey == selectedSource?.Key));
        var sourceChoice = dialog.Choice("Inspect file", reports.Select(r => r.File).ToArray(), selected);
        var reasons = new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new(0, 6, 0, 9) }; dialog.Add(reasons);
        var coverage = ReviewGrid(90);
        ReviewColumn(coverage, "Other file", nameof(SharedIdCount.OtherFile), 280);
        ReviewColumn(coverage, "Shared IDs", nameof(SharedIdCount.Shared), 110);
        ReviewColumn(coverage, "Complete IDs in this file", nameof(SharedIdCount.ThisFileIds), 170);
        ReviewColumn(coverage, "Complete IDs in other file", nameof(SharedIdCount.OtherFileIds), 175);
        ReviewColumn(coverage, "% of this file's complete IDs", nameof(SharedIdCount.Coverage), 185);
        dialog.Add(coverage);
        var controls = new Grid { Margin = new(0, 10, 0, 5) };
        controls.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); controls.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); controls.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var kind = new ComboBox { ItemsSource = new[] { "All problems", "Missing keys", "Duplicate keys", "Unmatched IDs", "Unconfigured keys" }, SelectedIndex = 0, Margin = new(0, 0, 10, 0) };
        var other = new ComboBox { Margin = new(0, 0, 10, 0) };
        var find = new TextBox { ToolTip = "Find text in an ID, additional key or time value", MinHeight = 33 };
        controls.Children.Add(kind); Grid.SetColumn(other, 1); controls.Children.Add(other); Grid.SetColumn(find, 2); controls.Children.Add(find); dialog.Add(controls);
        var counts = new TextBlock { FontSize = 11, Foreground = (Brush)FindResource("Muted"), Margin = new(0, 2, 0, 4) }; dialog.Add(counts);
        var issues = ReviewGrid(110);
        ReviewColumn(issues, "Source record", nameof(MatchIssue.Record), 105);
        ReviewColumn(issues, "Problem", nameof(MatchIssue.Problem), 130);
        ReviewColumn(issues, "ID", nameof(MatchIssue.Id), 130);
        ReviewColumn(issues, "Additional key", nameof(MatchIssue.Entity), 135);
        ReviewColumn(issues, "Grouping", nameof(MatchIssue.Grouping), 130);
        ReviewColumn(issues, "Details", nameof(MatchIssue.Detail), 480);
        dialog.Add(issues);
        MatchIssue? navigate = null; bool editKeys = false;
        var actions = new WrapPanel { Margin = new(0, 10, 0, 0) };
        var jump = SmallButton("Go to selected source record"); jump.IsEnabled = false;
        var edit = SmallButton("Edit this file's keys…");
        actions.Children.Add(jump); actions.Children.Add(edit); dialog.Add(actions);
        dialog.Description("Source record numbers exclude the CSV header. Jumping to a record clears row filters so the original working record is visible. No data is written, removed, or automatically repaired here.");
        void FillIssues()
        {
            var report = reports[Math.Max(0, sourceChoice.SelectedIndex)];
            IEnumerable<MatchIssue> list = report.Issues;
            list = kind.SelectedIndex switch { 1 => list.Where(i => i.Kind is MatchIssueKind.MissingId or MatchIssueKind.MissingEntity or MatchIssueKind.MissingGrouping), 2 => list.Where(i => i.Kind == MatchIssueKind.DuplicateKey), 3 => list.Where(i => i.Kind == MatchIssueKind.UnmatchedId), 4 => list.Where(i => i.Kind == MatchIssueKind.UnconfiguredKey), _ => list };
            if (other.SelectedIndex > 0) { string key = report.SharedIds[other.SelectedIndex - 1].OtherSourceKey; list = list.Where(i => i.Kind != MatchIssueKind.UnmatchedId || i.OtherSourceKey == key); }
            if (find.Text.Length > 0) list = list.Where(i => i.Id.Contains(find.Text, StringComparison.OrdinalIgnoreCase) || i.Entity.Contains(find.Text, StringComparison.OrdinalIgnoreCase) || i.Grouping.Contains(find.Text, StringComparison.OrdinalIgnoreCase));
            var filtered = list.OrderBy(i => i.Row).ThenBy(i => i.Kind).ToArray();
            issues.ItemsSource = filtered.Take(500).ToList(); issues.Height=Math.Min(110,Math.Max(60,filtered.Length*29+35)); counts.Text = $"{filtered.Length:N0} issues matching these controls" + (filtered.Length > 500 ? " · displaying the first 500; narrow by ID or problem" : "") + " · search box finds ID/additional-key/time text";
        }
        void FillSource()
        {
            var report = reports[Math.Max(0, sourceChoice.SelectedIndex)];
            reasons.Text = report.KeyStatus + $" · {report.MissingIdRows:N0} missing IDs · {report.MissingEntityRows:N0} missing additional keys · {report.MissingGroupingRows:N0} missing grouping values · {report.ExtraDuplicateRows:N0} extra duplicate records\n" + report.IdReason + " " + report.EntityReason + " " + report.GroupingReason;
            coverage.ItemsSource = report.SharedIds;
            other.ItemsSource = new[] { "Unmatched: all other files" }.Concat(report.SharedIds.Select(r => "Unmatched: " + r.OtherFile)).ToArray(); other.SelectedIndex = 0;
            FillIssues();
        }
        sourceChoice.SelectionChanged += (_, _) => FillSource(); kind.SelectionChanged += (_, _) => FillIssues(); other.SelectionChanged += (_, _) => FillIssues(); find.TextChanged += (_, _) => FillIssues();
        issues.SelectionChanged += (_, _) => jump.IsEnabled = issues.SelectedItem is MatchIssue;
        void Navigate() { if (issues.SelectedItem is not MatchIssue selectedIssue) return; navigate = selectedIssue; dialog.DialogResult = true; }
        jump.Click += (_, _) => Navigate(); issues.MouseDoubleClick += (_, _) => Navigate();
        edit.Click += (_, _) => { editKeys = true; dialog.DialogResult = true; };
        summary.SelectionChanged += (_, _) => { if (summary.SelectedItem is FileMatchReport report) sourceChoice.SelectedIndex = reports.IndexOf(report); };
        FillSource(); dialog.Finish("Close",false);
        if (editKeys) { SourceSettings(session.Sources.Single(s => s.Key == reports[sourceChoice.SelectedIndex].SourceKey)); return; }
        if (navigate != null) _ = NavigateToMatchIssue(navigate);
    }
    private async Task NavigateToMatchIssue(MatchIssue issue)
    {
        var source = session.Sources.FirstOrDefault(s => s.Key == issue.SourceKey); if (source == null) return;
        ClearStructuredFiltersForNavigation(); RowFilter.Clear(); filterTimer.Stop(); lastSearch=""; selectedSource = source; SetSourceMode(); await RefreshPreview();
        int rowIndex = view.Rows.FindIndex(row => row.Any(cell => cell.Origin is { } origin && origin.SourceKey == issue.SourceKey && origin.Row == issue.Row));
        var column = columnKeys.FirstOrDefault(pair => pair.Value == source.Key + ":" + issue.Column).Key ?? Preview.Columns.FirstOrDefault();
        var item = column == null ? null : ShowViewRow(rowIndex);
        if (item == null || column == null) { ShowNotice("Source file opened; the selected record is not visible in the current variable selection."); return; } Preview.SelectedCells.Clear(); Preview.CurrentCell = new(item, column); Preview.SelectedCells.Add(new(item, column)); Preview.ScrollIntoView(item, column); Preview.Focus();
        Status.Text = $"{source.Name} · source record {issue.Record:N0} · {issue.Problem}";
    }
    private static DataGrid ReviewGrid(double height) => new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserReorderColumns = false, SelectionUnit = DataGridSelectionUnit.FullRow, SelectionMode = DataGridSelectionMode.Single, Height = height, ColumnHeaderHeight = 32, RowHeight = 29, Margin = new(0, 4, 0, 4), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private static void ReviewColumn(DataGrid grid, string title, string path, double width) => grid.Columns.Add(new DataGridTextColumn { Header = title, Binding = new Binding(path), Width = width });
}
