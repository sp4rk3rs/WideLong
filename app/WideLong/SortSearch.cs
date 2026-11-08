using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WideLong;

// Search box in the SORT BY bar: find any output variable by name or source and add it to the sort order.
public partial class MainWindow
{
    private const int SortSearchLimit = 200;
    private bool suppressSortSearch;

    // Open on deliberate focus (click or Tab), not when the window is reactivated and focus is restored to the box.
    private void SortSearch_Focus(object sender, KeyboardFocusChangedEventArgs e) { if (ready && e.OldFocus != null && !suppressSortSearch) ShowSortMatches(); }
    private void SortSearch_MouseUp(object sender, MouseButtonEventArgs e) { if (ready && !SortSearchPopup.IsOpen) ShowSortMatches(); }
    private void SortSearch_Changed(object sender, TextChangedEventArgs e)
    {
        SortSearchHint.Visibility = SortSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ready && !suppressSortSearch && SortSearch.IsKeyboardFocusWithin) ShowSortMatches();
    }

    private void ShowSortMatches()
    {
        SortSearchResults.Items.Clear();
        if (hasError || view.Fields.Count == 0) { SortSearchPopup.IsOpen = false; return; }
        var sortedKeys = Preferences.Sorts.Select(s => s.Key).ToList();
        var matches = Presentation.SortCandidates(view.Fields, sortedKeys, SortSearch.Text, SortSearchLimit + 1);
        foreach (var field in matches.Take(SortSearchLimit))
        {
            var body = new StackPanel { Margin = new(2, 1, 2, 1) };
            body.Children.Add(new TextBlock { Text = field.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(new TextBlock { Text = field.Source + " · " + (field.Kind == ValueKind.Number ? "number" : field.Kind.ToString().ToLowerInvariant()), FontSize = 10, Foreground = (Brush)FindResource("Muted"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
            SortSearchResults.Items.Add(new ListBoxItem { Content = body, Tag = field.Key, Padding = new(6, 3, 6, 3) });
        }
        int remaining = view.Fields.Count - sortedKeys.Count(k => view.Fields.Any(f => f.Key == k));
        SortSearchSummary.Text = matches.Count == 0 ? (SortSearch.Text.Trim().Length == 0 ? "Every variable is already in the sort order." : "No variables match. Try part of the name or the source file.")
            : (matches.Count > SortSearchLimit ? $"Showing the first {SortSearchLimit} of {remaining:N0} variables · keep typing to narrow" : $"{matches.Count:N0} match{(matches.Count == 1 ? "" : "es")}") + " · Enter adds ascending · Shift+Enter descending";
        if (SortSearchResults.Items.Count > 0) SortSearchResults.SelectedIndex = 0;
        SortSearchPopup.IsOpen = true;
    }

    private void SortSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SortSearchPopup.IsOpen = false; e.Handled = true; return; }
        if (e.Key == Key.Down && SortSearchResults.Items.Count > 0)
        {
            if (!SortSearchPopup.IsOpen) ShowSortMatches();
            int next = Math.Min(SortSearchResults.Items.Count - 1, SortSearchResults.SelectedIndex + 1);
            SortSearchResults.SelectedIndex = next; SortSearchResults.ScrollIntoView(SortSearchResults.SelectedItem); e.Handled = true; return;
        }
        if (e.Key == Key.Up && SortSearchResults.Items.Count > 0)
        {
            SortSearchResults.SelectedIndex = Math.Max(0, SortSearchResults.SelectedIndex - 1); SortSearchResults.ScrollIntoView(SortSearchResults.SelectedItem); e.Handled = true; return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (!SortSearchPopup.IsOpen) ShowSortMatches();
            if ((SortSearchResults.SelectedItem ?? (SortSearchResults.Items.Count > 0 ? SortSearchResults.Items[0] : null)) is ListBoxItem { Tag: string key }) _ = AddSortFromSearch(key, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }
    }
    private void SortSearchResults_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SortSearchResults.SelectedItem is ListBoxItem { Tag: string key }) { e.Handled = true; _ = AddSortFromSearch(key, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); }
        else if (e.Key == Key.Escape) { e.Handled = true; SortSearchPopup.IsOpen = false; SortSearch.Focus(); }
    }
    private void SortSearchResults_Click(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject) is { Tag: string key }) { e.Handled = true; _ = AddSortFromSearch(key, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); }
    }

    private async Task AddSortFromSearch(string key, bool descending)
    {
        if (busy || !await CommitPendingEdit()) return;
        var field = view.Fields.FirstOrDefault(f => f.Key == key);
        if (field == null) return;
        SortSearchPopup.IsOpen = false;
        if (Preferences.Sorts.Any(s => s.Key == key)) { Status.Text = field.Name + " is already in the sort order"; return; }
        Preferences.Sorts.Add(new(key, descending)); Preferences.MoveSortsToFront();
        RecordActivity("View", "Sorted " + field.Name + (descending ? " descending" : " ascending") + " (from variable search)");
        suppressSortSearch = true; try { SortSearch.Clear(); } finally { suppressSortSearch = false; }
        await RefreshPreview(rebuild: false, resetPage: true);
        // Sorted variables now sit at the far left, so bring the grid back to its first column.
        if (Preview.Items.Count > 0 && Preview.Columns.Count > 0) Preview.ScrollIntoView(Preview.Items[0], Preview.Columns.OrderBy(c => c.DisplayIndex).First());
        int priority = Preferences.Sorts.Where(s => view.Fields.Any(f => f.Key == s.Key)).ToList().FindIndex(s => s.Key == key) + 1;
        Status.Text = $"Sorting by {field.Name} {(descending ? "descending" : "ascending")} · priority {priority} · use the chip arrow to reverse or drag chips to reorder";
    }
}
