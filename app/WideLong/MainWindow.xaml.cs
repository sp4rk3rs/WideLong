using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Data;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WideLong;

public partial class MainWindow : Window
{
    private Session session = new();
    private readonly Stack<Session> undo = new(), redo = new();
    private Layout layout = Layout.PersonTime;
    private Source? selectedSource;
    private View view = new();
    private bool ready, busy, hasError, buildingColumns;
    private long generation;
    private string lastSearch = "";
    private readonly DispatcherTimer filterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly HashSet<string> collapsedSources = new();
    private readonly Dictionary<string, ViewPreferences> preferences = new(StringComparer.Ordinal);
    private readonly Dictionary<DataGridColumn, string> columnKeys = new();
    private readonly Dictionary<DataGridColumn, int> columnIndexes = new();
    private readonly Dictionary<DataGridColumn, OutputField> columnFields = new();
    private readonly DispatcherTimer variableTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly ConditionalWeakTable<FrameworkElement, Func<ContextMenu>> menuFactories = new();
    private static readonly ContextMenu PlaceholderMenu = new();
    // Preview paging: only one page of rows is materialized in the grid. Sorting, filtering and export use every row.
    private static readonly int[] PageSizes = [50, 100, 250, 500, 1000];
    private int pageStart, pageSize = 100;
    // The merged (unsorted, unfiltered) view is cached so sorting, searching and paging do not rebuild it.
    private sealed record RawKey(Session Session, Layout Layout, MatchMode Match, bool Stack, string? SourceKey, string RowVariables);
    // Up to three recently shown layouts stay in memory (never on disk); any data change clears them.
    private readonly List<(RawKey Key, View View)> rawCache = new();
    private const int RawCacheLimit = 3;
    // Older cached layouts are released once the cache holds more than this many cells (wide study files reach millions).
    private const long RawCacheCellBudget = 12_000_000;

    private ViewPreferences Preferences
    {
        get
        {
            string context = layout == Layout.Source ? "Source:" + (selectedSource ?? session.Sources.FirstOrDefault())?.Key : layout + ":" + CombineMode.SelectedIndex;
            if (!preferences.TryGetValue(context, out var p)) preferences[context] = p = new();
            return p;
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        var screen=SystemParameters.WorkArea;
        MinWidth=Math.Min(MinWidth,screen.Width);MinHeight=Math.Min(MinHeight,screen.Height);
        Width=Math.Min(Width,screen.Width);Height=Math.Min(Height,screen.Height);
        InitializeHover(); ready = true;
        filterTimer.Tick += (_, _) => { filterTimer.Stop(); if(lastSearch!=RowFilter.Text){lastSearch=RowFilter.Text;RecordActivity("View",lastSearch.Length==0?"Cleared row search":"Search rows: "+lastSearch);} _ = RefreshPreview(rebuild: false, resetPage: true); };
        variableTimer.Tick += (_, _) => { variableTimer.Stop(); RenderSources(); };
        Preview.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(HeaderDown), true);
        Preview.AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(HeaderMove), true);
        Preview.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(HeaderUp), true);
        Preview.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(HeaderUpDone), true);
        Preview.AddHandler(KeyDownEvent, new KeyEventHandler(HeaderKey), true);
        Deactivated += (_, _) => RecoverHeaderDrag();
        // Any release anywhere in the window (including outside the grid) clears a stranded column-drag image.
        AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _ = Dispatcher.BeginInvoke(RecoverHeaderDrag, DispatcherPriority.Input)), true);
        MouseLeave += (_, _) => { if (Mouse.LeftButton != MouseButtonState.Pressed) RecoverHeaderDrag(); };
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            if (e.Key == Key.Z) { Undo_Click(this, new()); e.Handled = true; }
            if (e.Key == Key.Y) { Redo_Click(this, new()); e.Handled = true; }
            if (e.Key == Key.O) { Import_Click(this, new()); e.Handled = true; }
            if (e.Key == Key.PageDown) { NextPage_Click(this, new()); e.Handled = true; }
            if (e.Key == Key.PageUp) { PrevPage_Click(this, new()); e.Handled = true; }
        };
        RenderSources(); _ = RefreshPreview();
    }

    public void StartSyntheticCheck(bool compound=false)
    {
        Title = "WideLong 0.5.3 · synthetic verification"+(compound?" · compound keys":"");
        session = compound?Examples.CompoundFixture():Demo.Create(); selectedSource = session.Sources[0];
        ExampleLabel.Visibility = Visibility.Visible; ExampleName.Text = compound ? "Household" : "Health study";
        RenderSources(); _ = RefreshPreview(); Status.Text = compound?"Fictional household example · ID + person + time · imports disabled":"Fictional study · 600 participants · imports disabled in this verification window";
    }

    private static Button SmallButton(string text) => new() { Content = text, Padding = new(7, 3, 7, 3), FontSize = 11 };
    private void RenderSources()
    {
        SourcesPanel.Children.Clear();
        if (session.Sources.Count == 0)
        {
            SourcesPanel.Children.Add(new TextBlock { Text = "Your files will appear here.", Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new(0, 12, 0, 0) }); return;
        }
        string search = VariableSearch.Text;
        foreach (var source in session.Sources)
        {
            var expander = new Expander { IsExpanded = !collapsedSources.Contains(source.Key), Margin = new(0, 0, 0, 16) };
            var heading = new StackPanel();
            heading.Children.Add(new TextBlock { Text = source.Name, FontWeight = FontWeights.SemiBold, MaxWidth = 208, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            heading.Children.Add(new TextBlock { Text = $"{source.Rows.Count:N0} rows · {source.Fields.Count} columns", FontSize = 11, Foreground = (Brush)FindResource("Muted"), Margin = new(0, 3, 0, 4) });
            expander.Header = heading;
            expander.Collapsed += (_, _) => collapsedSources.Add(source.Key); expander.Expanded += (_, _) => collapsedSources.Remove(source.Key);
            // Collapsed files build their variable rows only when first expanded.
            StackPanel BuildPanel()
            {
            var panel = new StackPanel { Margin = new(4, 3, 0, 0) };
            var tools = new WrapPanel { Margin = new(0, 0, 0, 7) };
            var settings = SmallButton("ID / grouping"); settings.ToolTip = source.IdReason; settings.Click += (_, _) => SourceSettings(source);
            var sourceRows = SmallButton("View file"); sourceRows.Click += (_, _) => { selectedSource = source; SetSourceMode(); };
            tools.Children.Add(settings); tools.Children.Add(sourceRows); panel.Children.Add(tools);
            var selection = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 7) };
            var all = SmallButton("All"); var none = SmallButton("Clear");
            all.Click += (_, _) => Change(() => { foreach (var f in source.Fields) f.Keep = true; }, "Selected all columns from " + source.Name);
            none.Click += (_, _) => Change(() => { for (int i = 0; i < source.Fields.Count; i++) source.Fields[i].Keep = i == source.IdColumn || i == source.TimeColumn || i==source.EntityColumn; }, "Kept only required keys from " + source.Name);
            selection.Children.Add(all); selection.Children.Add(none); panel.Children.Add(selection);
            for (int c = 0; c < source.Fields.Count; c++)
            {
                int column = c; var field = source.Fields[c];
                if (search.Length > 0 && !field.Name.Contains(search, StringComparison.OrdinalIgnoreCase) && !source.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                bool key = c == source.IdColumn || c == source.TimeColumn || c==source.EntityColumn;
                var row = new Grid { Margin = new(0, 0, 0, 2) };
                row.ColumnDefinitions.Add(new() { Width = new(23) }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                var check = new CheckBox { IsChecked = field.Keep || key, IsEnabled = !key, VerticalAlignment = VerticalAlignment.Center, ToolTip = key ? "Required to match records. Change in ID / grouping." : "Keep this variable in the output" };
                check.Click += (_, _) => Change(() => field.Keep = check.IsChecked == true, "Updated columns to keep"); row.Children.Add(check);
                var name = new Button { Content = new TextBlock { Text = field.Name + (c == source.IdColumn ? "  · ID" : c == source.EntityColumn ? "  · extra key" : c == source.TimeColumn ? "  · time/group" : "") + (source.KeepRules.Any(k => k.Column == column) ? "  · filtered" : ""), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis }, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new(5, 5, 2, 5), BorderThickness = new(0), Margin = new(0), Background = Brushes.Transparent, FontSize = 12, ToolTip = "Find this column in the grid. Right-click for actions." };
                name.Click += (_, _) => FocusSourceColumn(source, column);
                LazyMenu(name, () => SourceMenu(source, column)); AttachHover(name);
                Grid.SetColumn(name, 1); row.Children.Add(name); panel.Children.Add(row);
            }
            return panel;
            }
            if (expander.IsExpanded) expander.Content = BuildPanel();
            else expander.Expanded += (_, _) => expander.Content ??= BuildPanel();
            SourcesPanel.Children.Add(expander);
        }
    }

    private void FocusSourceColumn(Source source, int column)
    {
        selectedSource = source;
        if (layout == Layout.Source) { _ = RefreshPreview(rebuild: false, resetPage: true); return; }
        string key = source.Key + ":" + column;
        if (source.Fields[column].Measure.Length > 0) key = source.Key + ":measure:" + source.Fields[column].Measure;
        string? occasion=layout==Layout.Person&&source.Fields[column].Measure.Length>0?source.Fields[column].Occasion:null;
        if(column==source.IdColumn) key="@person_id";
        else if(column==source.EntityColumn && layout is Layout.PersonTime or Layout.Measurement) key="@entity";
        else if(column==source.TimeColumn && layout is Layout.PersonTime or Layout.Measurement) key="@time";
        var target = columnKeys.FirstOrDefault(p => (p.Value == key || p.Value.StartsWith(key + "\u001f", StringComparison.Ordinal)) && (occasion==null||p.Value.EndsWith("\u001ftime:"+occasion,StringComparison.Ordinal))).Key;
        if (target != null && Preview.Items.Count > 0) Preview.ScrollIntoView(Preview.Items[0], target);
        else ShowNotice("This variable is hidden in this layout. Use View file to edit its source values.");
    }

    private void Remember()
    {
        undo.Push(session.Copy()); redo.Clear();
        if (undo.Count > 12) { var keep = undo.Take(12).Reverse().ToArray(); undo.Clear(); foreach (var item in keep) undo.Push(item); }
    }
    private void Change(Action action, string status)
    {
        Remember(); var before=undo.Peek(); action(); RecordDataChange(before,status); Status.Text = status; RenderSources(); _ = RefreshPreview();
    }

    private async Task RefreshPreview(bool rebuild = true, bool resetPage = false)
    {
        if (!ready) return;
        CloseHover(); UpdateLayoutHelp(); FilterSummaryText.Text=StructuredFilterSummary;
        long ticket = ++generation;
        RecoverHeaderDrag();
        if (rebuild) rawCache.Clear(); // Data changed: a superseded rebuild must never leave a stale view behind.
        busy = true; ExportButton.IsEnabled = false; CodeButton.IsEnabled = false; SourcesPanel.IsEnabled = false;
        Counts.Text = "Preparing preview…";
        try
        {
            var sourceKey = selectedSource?.Key;
            Layout chosenLayout = layout; var match = (MatchMode)MatchRule.SelectedIndex;
            bool stack = CombineMode.SelectedIndex == 1;
            string filter = RowFilter.Text; var prefs = Preferences.Copy(); var filterRules=structuredFilter.Copy();
            var key = new RawKey(session, chosenLayout, match, stack, chosenLayout == Layout.Source ? sourceKey : null,
                chosenLayout == Layout.Measurement && prefs.RowVariables != null ? string.Join("\u001f", prefs.RowVariables.Order(StringComparer.Ordinal)) : "");
            int hitIndex = rawCache.FindIndex(c => c.Key == key);
            View? cached = hitIndex >= 0 ? rawCache[hitIndex].View : null;
            // Copy the session only when the merged view must be rebuilt; the copy keeps background work isolated from edits.
            var snapshot = cached == null ? session.Copy(includeOriginals: false) : null;
            var computed = await Task.Run(() =>
            {
                View Build(Session snap) => chosenLayout == Layout.Source
                    ? snap.Sources.Count == 0 ? new View() : Engine.SourceView(snap.Sources.FirstOrDefault(s => s.Key == sourceKey) ?? snap.Sources[0])
                    : Engine.Build(snap, chosenLayout, match, stack, prefs.RowVariables);
                var raw = cached ?? Build(snapshot!);
                return (Raw: raw, Shown: Presentation.Apply(Filtering.Apply(raw,filterRules), prefs, filter));
            });
            if (ticket != generation) return;
            rawCache.RemoveAll(c => c.Key == key); rawCache.Insert(0, (key, computed.Raw));
            long cachedCells = 0; int keep = 0;
            foreach (var (_, cachedView) in rawCache)
            {
                long cells = (long)cachedView.Rows.Count * Math.Max(1, cachedView.Fields.Count);
                if (keep == RawCacheLimit || (keep > 0 && cachedCells + cells > RawCacheCellBudget)) break;
                cachedCells += cells; keep++;
            }
            rawCache.RemoveRange(keep, rawCache.Count - keep);
            view = computed.Shown; hasError = false; Preferences.Reconcile(view.Fields.Select(f => f.Key));
            buildingColumns = true;
            BuildColumns();
            if (resetPage) pageStart = 0;
            RenderPage(); RenderSortChips();
            EmptyState.Visibility = session.Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            // An empty grid still draws its header band, which covered the start message.
            Preview.Visibility = session.Sources.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Counts.Text = $"{view.Rows.Count:N0} rows × {view.Fields.Count:N0} columns" + (filter.Length > 0 || structuredFilter.Rules.Count > 0 ? " · filtered" : "");
            Notice.Text = session.Sources.Count == 0 ? "Import CSVs or try the fictional example to begin." : layout == Layout.Source ? "Source rows. Changes affect every layout; originals stay untouched." : string.Join(" · ", view.Notes.Take(1));
            NoticeBorder.Background = new SolidColorBrush(Color.FromRgb(238, 232, 245)); ExportButton.IsEnabled = view.Fields.Count > 0; CodeButton.IsEnabled = view.Fields.Count > 0;
        }
        catch (Exception ex)
        {
            if (ticket != generation) return;
            hasError = true; view = new(); Preview.ItemsSource = null; Preview.Columns.Clear(); columnKeys.Clear(); columnIndexes.Clear(); columnFields.Clear(); RenderSortChips(); UpdatePager(); EmptyState.Visibility = Visibility.Collapsed; Preview.Visibility = Visibility.Visible;
            Notice.Text = ex is OutOfMemoryException ? "Not enough memory for this operation. Select fewer columns or smaller files; no data was written to disk." : ex is InvalidOperationException or ArgumentException ? ex.Message : "The preview could not be prepared (" + ex.GetType().Name + "). Your data is unchanged; undo the last step or check ID / grouping.";
            NoticeBorder.Background = new SolidColorBrush(Color.FromRgb(255, 241, 219)); Counts.Text = layout == Layout.Measurement ? "Choose which variables should become rows" : "Check ID / grouping or use View file to resolve the mapping";
        }
        finally
        {
            if (ticket == generation) { buildingColumns = false; busy = false; SourcesPanel.IsEnabled = true; UndoButton.IsEnabled = undo.Count > 0; RedoButton.IsEnabled = redo.Count > 0; _ = Dispatcher.BeginInvoke(RecoverHeaderDrag, DispatcherPriority.Input); }
        }
    }

    /// <summary>Reuses existing grid columns when the visible fields are unchanged; only headers are redecorated.</summary>
    private void BuildColumns()
    {
        var active = Preferences.Sorts.Where(s => view.Fields.Any(f => f.Key == s.Key)).ToList();
        bool reuse = Preview.Columns.Count == view.Fields.Count && Preview.Columns.All(c => columnIndexes.TryGetValue(c, out int i) && i < view.Fields.Count && columnKeys[c] == view.Fields[i].Key);
        if (!reuse)
        {
            Preview.ItemsSource = null; Preview.Columns.Clear(); columnKeys.Clear(); columnIndexes.Clear(); columnFields.Clear();
            for (int c = 0; c < view.Fields.Count; c++)
            {
                var field = view.Fields[c];
                var column = new DataGridTextColumn { Binding = new Binding("c" + c) { TargetNullValue = "—" }, SortMemberPath = "c" + c, MinWidth = 110, Width = new DataGridLength(field.Name.Length > 16 ? 195 : 160) };
                columnKeys[column] = field.Key; columnIndexes[column] = c; columnFields[column] = field;
                DecorateColumn(column, field, active); Preview.Columns.Add(column);
            }
            return;
        }
        // Display order is preserved by DisplayIndex; data binding uses the field index.
        foreach (var column in Preview.Columns.OrderBy(c => columnIndexes[c]))
        {
            var field = view.Fields[columnIndexes[column]];
            columnFields[column] = field; DecorateColumn(column, field, active);
            if (column.DisplayIndex != columnIndexes[column]) column.DisplayIndex = columnIndexes[column];
        }
    }

    /// <summary>Shows a message that needs attention in the notice bar until the next preview refresh.</summary>
    private void ShowNotice(string message)
    {
        Status.Text = message; Notice.Text = message;
        NoticeBorder.Background = new SolidColorBrush(Color.FromRgb(255, 241, 219));
    }

    private void RenderPage()
    {
        int total = view.Rows.Count;
        if (pageStart >= total) pageStart = total == 0 ? 0 : (total - 1) / pageSize * pageSize;
        if (pageStart < 0) pageStart = 0;
        var table = new DataTable();
        for (int c = 0; c < view.Fields.Count; c++) table.Columns.Add("c" + c, typeof(string));
        table.BeginLoadData();
        int end = Math.Min(total, pageStart + pageSize);
        for (int r = pageStart; r < end; r++) table.Rows.Add(view.Rows[r].Select(c => c.Missing ? (object)DBNull.Value : c.Value).ToArray());
        table.EndLoadData();
        Preview.ItemsSource = table.DefaultView;
        UpdatePager();
    }
    private void UpdatePager()
    {
        int total = view.Rows.Count, end = Math.Min(total, pageStart + pageSize);
        PageText.Text = total == 0 ? "No rows" : total <= pageSize ? $"All {total:N0} rows shown" : $"Rows {pageStart + 1:N0}–{end:N0} of {total:N0}";
        FirstPageButton.IsEnabled = PrevPageButton.IsEnabled = pageStart > 0;
        NextPageButton.IsEnabled = LastPageButton.IsEnabled = end < total;
    }
    private async void GoToPage(int start)
    {
        if (busy || hasError || view.Rows.Count == 0 || !await CommitPendingEdit()) return;
        int last = (view.Rows.Count - 1) / pageSize * pageSize;
        start = Math.Clamp(start, 0, last);
        if (start == pageStart) return;
        pageStart = start; CloseHover(); RenderPage();
        if (Preview.Items.Count > 0) Preview.ScrollIntoView(Preview.Items[0]);
    }
    private void FirstPage_Click(object sender, RoutedEventArgs e) => GoToPage(0);
    private void PrevPage_Click(object sender, RoutedEventArgs e) => GoToPage(pageStart - pageSize);
    private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(pageStart + pageSize);
    private void LastPage_Click(object sender, RoutedEventArgs e) => GoToPage(int.MaxValue);
    private async void PageSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || PageSizeChoice.SelectedIndex < 0) return;
        int size = PageSizes[PageSizeChoice.SelectedIndex];
        if (size == pageSize) return;
        if (busy || !await CommitPendingEdit()) { PageSizeChoice.SelectedIndex = Array.IndexOf(PageSizes, pageSize); return; }
        pageStart = pageStart / size * size; pageSize = size;
        if (!hasError) RenderPage();
        Status.Text = $"Preview shows {size:N0} rows at a time · sorting, filters and export still use every row";
    }
    /// <summary>Moves the pager to a row of the full view and returns its grid item.</summary>
    private object? ShowViewRow(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= view.Rows.Count) return null;
        int start = rowIndex / pageSize * pageSize;
        if (start != pageStart) { pageStart = start; RenderPage(); }
        int local = rowIndex - pageStart;
        return local < Preview.Items.Count ? Preview.Items[local] : null;
    }

    /// <summary>Builds context menus only when opened or hovered; hundreds of variables no longer allocate menus on every refresh.</summary>
    private void LazyMenu(FrameworkElement element, Func<ContextMenu> factory)
    {
        menuFactories.AddOrUpdate(element, factory);
        element.ContextMenu = PlaceholderMenu;
        element.ContextMenuOpening += (_, _) => { if (menuFactories.TryGetValue(element, out var f)) element.ContextMenu = f(); };
    }
    private ContextMenu? MenuFor(FrameworkElement anchor)
    {
        if (anchor is DataGridColumnHeader { Column: { } column } && columnFields.TryGetValue(column, out var field)) return OutputMenu(field);
        if (menuFactories.TryGetValue(anchor, out var factory)) return factory();
        return anchor.ContextMenu == PlaceholderMenu ? null : anchor.ContextMenu;
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        layout = Enum.Parse<Layout>((sender as RadioButton)?.Tag?.ToString() ?? "PersonTime");
        RecordActivity("View", "Viewing " + ShapeName(layout));
        MatchRule.IsEnabled = layout != Layout.Source && CombineMode.SelectedIndex == 0; _ = RefreshPreview(rebuild: false, resetPage: true);
    }
    private void SetSourceMode()
    {
        SourceMode.IsChecked = true;
        if (layout == Layout.Source) _ = RefreshPreview(rebuild: false, resetPage: true);
    }
    private void Combine_Changed(object sender, SelectionChangedEventArgs e) { if (ready) { RecordActivity("View", "Combine files: " + (CombineMode.SelectedIndex==1?"Stack matching columns":"Match IDs and groups") + "; IDs to keep: " + ((ComboBoxItem)MatchRule.SelectedItem)?.Content); MatchRule.IsEnabled = CombineMode.SelectedIndex == 0 && layout != Layout.Source; _ = RefreshPreview(rebuild: false, resetPage: true); } }
    private void VariableSearch_Changed(object sender, TextChangedEventArgs e) { if (ready) { variableTimer.Stop(); variableTimer.Start(); } }
    private void Filter_Changed(object sender, TextChangedEventArgs e) { if (ready) { filterTimer.Stop(); filterTimer.Start(); } }

    private void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var chooser = new FormDialog(this, "Choose an example", "All examples are fictional and run locally. Pick the structure that looks most like your files.");
        chooser.Width = 680;
        ExampleDefinition selected = Examples.All[0];
        foreach (var example in Examples.All)
        {
            var content = new StackPanel { MaxWidth = 570 };
            content.Children.Add(new TextBlock { Text = example.Title, FontWeight = FontWeights.SemiBold });
            content.Children.Add(new TextBlock { Text = example.Description, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0x5E, 0x52, 0x69)), Margin = new(0, 3, 0, 0) });
            var radio = new RadioButton { Content = content, GroupName = "Example", IsChecked = example == selected, Margin = new(0, 6, 0, 7), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            radio.Checked += (_, _) => selected = example; chooser.Add(radio);
        }
        if (chooser.Finish("Open example") != true) return;
        if (session.Sources.Count > 0 && MessageBox.Show(this, "Replace this session with the selected fictional example? Nothing saves automatically. You can undo the data replacement.", "Open example", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Change(() =>
        {
            session = selected.Create(); selectedSource = session.Sources[0]; preferences.Clear(); structuredFilter=new(); RowFilter.Clear(); VariableSearch.Clear();
            CombineMode.SelectedIndex = selected.Stack ? 1 : 0; MatchRule.SelectedIndex = 0; TimeMode.IsChecked = true; layout = Layout.PersonTime;
            ExampleLabel.Visibility = Visibility.Visible; ExampleName.Text = selected.Title.Split(" · ")[0];
        }, selected.Title + " · " + selected.TryThis);
        OfferNumericTypes(session.Sources.ToList());
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (Title.Contains("synthetic verification", StringComparison.Ordinal)) { MessageBox.Show(this, "This verification window uses only fictional data. Use the normal application to import files."); return; }
        if (busy) return;
        var picker = new OpenFileDialog { Filter = "CSV / delimited files|*.csv;*.tsv;*.txt|All files|*.*", Multiselect = true, Title = "Import CSV files into memory" };
        if (picker.ShowDialog(this) != true) return;
        var dialog = new FormDialog(this, "Import CSV", "Choose how these files are separated and encoded. Originals remain unchanged.");
        var delimiter = dialog.Choice("Columns are separated by", ["Comma (,)", "Semicolon (;)", "Tab", "Pipe (|)"], picker.FileNames.All(f => Path.GetExtension(f).Equals(".tsv", StringComparison.OrdinalIgnoreCase)) ? 2 : 0);
        var missing = MissingControls(dialog, new HashSet<string> { "" });
        var encoding = dialog.Choice("Text encoding", ["UTF-8", "UTF-16 little endian", "Windows-1252"], 0);
        dialog.Description("The first row contains column names. Values start as text so leading zeros and literal NA/NULL are preserved.");
        if (dialog.Finish("Import") != true) return;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding decoder = encoding.SelectedIndex switch { 1 => new UnicodeEncoding(false, true, true), 2 => Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), _ => new UTF8Encoding(false, true) };
            char separator = new[] { ',', ';', '\t', '|' }[delimiter.SelectedIndex]; var imported = new List<Source>();
            foreach (string path in picker.FileNames)
            {
                using var reader = new StreamReader(path, decoder, detectEncodingFromByteOrderMarks: false);
                var source = Csv.Import(reader.ReadToEnd(), Path.GetFileName(path), separator); source.ImportPath = Path.GetFullPath(path);
                source.MissingTokens = ReadMissing(missing);
                source.EncodingName = encoding.SelectedIndex switch { 1 => "UTF-16LE", 2 => "windows-1252", _ => "UTF-8" };
                string root = Path.GetFileNameWithoutExtension(source.Name); int n = 2;
                while (session.Sources.Concat(imported).Any(s => s.Name == source.Name)) source.Name = root + " (" + n++ + ").csv";
                imported.Add(source);
            }
            Change(() => { session.Sources.AddRange(imported); KeyRecommendations.Recommend(session); selectedSource = imported[0]; ExampleLabel.Visibility = Visibility.Collapsed; }, $"Imported {imported.Count} file(s) · review ID / grouping");
            OfferNumericTypes(imported);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidOperationException or OutOfMemoryException)
        { MessageBox.Show(this, ex is OutOfMemoryException ? "Not enough memory to import these files. Nothing was saved." : ex.Message, "Import not completed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void SourceSettings(Source source)
    {
        if (busy) return;
        var d = new FormDialog(this, "Match " + source.Name, source.IdReason);
        var id = d.Choice("ID column", new[] { "Choose…" }.Concat(source.Fields.Select(f => f.Name)).ToArray(), source.IdColumn + 1);
        var entity=d.Choice("Additional matching column (optional)",new[]{"None — no role/entity key"}.Concat(source.Fields.Select(f=>f.Name)).ToArray(),source.EntityColumn+1);
        d.Description(source.EntityReason);
        var time = d.Choice("Time / repeated grouping column (optional)", new[] { "None — time may be in repeated column names" }.Concat(source.Fields.Select(f => f.Name)).ToArray(), source.TimeColumn + 1);
        d.Description("Match on ID + any additional key + time. For example, ID + person + time separates patient, spouse and informant records. For wide files, choose person here and leave time as None when time is mapped from column names. Files without an additional key apply to all roles/entities for their ID.");
        d.Description(source.GroupReason);
        var missing = MissingControls(d, source.MissingTokens, collapsed:true);
        var remove = new CheckBox { Content = "Remove this file from the session", Margin = new(0, 12, 0, 0) }; d.Add(remove);
        if (d.Finish("Apply") != true) return;
        if(remove.IsChecked!=true)
        {
            var keys=new[]{id.SelectedIndex-1,entity.SelectedIndex-1,time.SelectedIndex-1}.Where(c=>c>=0).ToArray();
            if(keys.Distinct().Count()!=keys.Length){MessageBox.Show(this,"Choose different columns for ID, additional key and time.","Matching columns",MessageBoxButton.OK,MessageBoxImage.Information);return;}
            var proposed=source.Copy();proposed.MissingTokens=ReadMissing(missing);
            var invalid=Values.ValidatePolicies(proposed);
            if(invalid.Count>0){MessageBox.Show(this,$"These file defaults would expose {invalid.Count:N0} values that do not match their current types. Keep their missing rules, recode the values, or use a variable-level override first.","Missing-value defaults",MessageBoxButton.OK,MessageBoxImage.Information);return;}
        }
        Change(() =>
        {
            if (remove.IsChecked == true) { session.Sources.Remove(source); if (selectedSource == source) selectedSource = session.Sources.FirstOrDefault(); return; }
            source.MissingTokens = ReadMissing(missing); source.IdChosen = true; source.GroupChosen = true; source.EntityChosen=true; source.EntityColumn=entity.SelectedIndex-1; source.IdColumn = id.SelectedIndex - 1; source.TimeColumn = time.SelectedIndex - 1;
            if (source.IdColumn >= 0) source.Fields[source.IdColumn].Keep = true; if (source.TimeColumn >= 0) source.Fields[source.TimeColumn].Keep = true;
            if(source.EntityColumn>=0) source.Fields[source.EntityColumn].Keep=true;
        }, "Updated matching columns");
    }

    private Origin? CellOrigin(DataGridRow row, DataGridColumn column)
    {
        if (row.Item is not DataRowView data || !columnIndexes.TryGetValue(column, out int c)) return null;
        int r = data.Row.Table.Rows.IndexOf(data.Row); if (r < 0) return null; r += pageStart;
        return r >= 0 && r < view.Rows.Count && c < view.Fields.Count ? view.Rows[r][c].Origin : null;
    }
    private void Preview_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        CloseHover(); if (e.EditingElement is TextBox input && e.Row.Item is DataRowView data && data[e.Column.SortMemberPath] == DBNull.Value) input.Text = "";
        if (busy || CellOrigin(e.Row, e.Column) == null) { ShowNotice("This cell is generated or absent from a source. Use View file to edit IDs or grouping values."); Dispatcher.BeginInvoke(() => Preview.CancelEdit()); }
    }
    private void Preview_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || busy) return;
        var origin = CellOrigin(e.Row, e.Column); if (origin == null) { e.Cancel = true; return; }
        var source = session.Sources.Single(s => s.Key == origin.SourceKey); var field = source.Fields[origin.Column]; string value;
        try { value = Values.ConvertWithMissing(((TextBox)e.EditingElement).Text, field.Kind, "yyyy-MM-dd", source.EffectiveMissingTokens(origin.Column)); }
        catch (InvalidOperationException ex) { e.Cancel = true; MessageBox.Show(this, ex.Message); return; }
        if (source.Rows[origin.Row][origin.Column] != value) Dispatcher.BeginInvoke(() => Change(() => { source.CaptureOriginal(); source.Steps.Add(new(StepKind.Cell, origin.Column, origin.Row, source.Rows[origin.Row][origin.Column], value)); source.Rows[origin.Row][origin.Column] = value; }, "Edited " + field.Name + " in " + source.Name + ", data record " + (origin.Row + 1)));
    }
    private async Task<bool> CommitPendingEdit()
    {
        if (!Preview.CommitEdit(DataGridEditingUnit.Cell, true) || !Preview.CommitEdit(DataGridEditingUnit.Row, true)) return false;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); return true;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { if (busy || undo.Count == 0) return; redo.Push(session.Copy()); session = undo.Pop(); if(undoActivities.TryPop(out var activity)){activity.State="Undone";redoActivities.Push(activity);RecordActivity("Undo",activity.Description);} RestoreSelection(); Status.Text = "Undid data change"; RenderSources(); _ = RefreshPreview(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (busy || redo.Count == 0) return; undo.Push(session.Copy()); session = redo.Pop(); if(redoActivities.TryPop(out var activity)){activity.State="Applied";undoActivities.Push(activity);RecordActivity("Redo",activity.Description);} RestoreSelection(); Status.Text = "Redid data change"; RenderSources(); _ = RefreshPreview(); }
    private void RestoreSelection() => selectedSource = session.Sources.FirstOrDefault(s => s.Key == selectedSource?.Key) ?? session.Sources.FirstOrDefault();

    private string? lastExportName;
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (busy || hasError || view.Fields.Count == 0 || !await CommitPendingEdit()) return;
        filterTimer.Stop(); await RefreshPreview(rebuild: false); if (busy || hasError) return;
        CloseHover();
        var options = new FormDialog(this, "Export data", "Name the dataset, choose its shape and the software you will use. Exporting a different shape leaves your preview as it is.") { Width = 610 };
        string suggested = lastExportName ?? (session.Sources.Count == 1 ? Path.GetFileNameWithoutExtension(session.Sources[0].Name) + "_prepared" : "prepared_data");
        var nameBox = options.Input("Dataset name", suggested);
        var nameHint = new TextBlock { FontSize = 11, Foreground = (Brush)FindResource("Muted"), Margin = new(0, 4, 0, 4), TextWrapping = TextWrapping.Wrap }; options.Add(nameHint);
        var shapes = new List<(string Label, Layout? Shape)> { ("Wide", Layout.Person), ("Long", Layout.PersonTime), ("Both — wide and long (ZIP)", null) };
        if (layout is Layout.Measurement or Layout.Source) shapes.Add((ShapeName(layout), layout));
        Layout? chosen = layout; Action refresh = () => { };
        foreach (var item in shapes) {
            bool current = item.Shape == layout;
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = item.Label + (current ? "   ● Current view" : ""), FontWeight = current ? FontWeights.Bold : FontWeights.SemiBold, Foreground = current ? (Brush)FindResource("Accent") : (Brush)FindResource("Ink") });
            body.Children.Add(new TextBlock { Text = item.Shape == Layout.Person ? "One row per ID; roles/entities and repeated observations use separate columns." : item.Shape == Layout.PersonTime ? "One row per ID + any additional key + time/grouping value." : item.Shape == null ? "Includes separate wide and long folders, each with its own files." : "Exports the layout currently displayed.", FontSize = 12, Margin = new(0,4,0,0) });
            bool supported = CombineMode.SelectedIndex != 1 || item.Shape is not null && item.Shape != Layout.Person;
            var radio = new RadioButton { Content = body, GroupName = "ExportShape", IsChecked = current, IsEnabled = supported, Margin = new(0,8,0,10) };
            radio.Checked += (_, _) => { chosen = item.Shape; refresh(); }; options.Add(radio);
        }
        if (CombineMode.SelectedIndex == 1) options.Description("Wide exports require matching files. Use Match IDs and groups to combine repeated observations across files; stacking keeps each source's records separate.");
        var target = options.Choice("Software", ["R — missing values as NA", "SAS — numeric missing as .; character missing as blank", "Other software — CSV with blank missing values"], 0);
        var package = options.Choice("Files", ["ZIP — dataset, dictionary, import script and cleaning code (recommended)", "CSV only"], 0);
        var includeCode = new CheckBox { Content = new TextBlock { Text = "Include the cleaning code: a commented script that rebuilds this dataset from your original files", TextWrapping = TextWrapping.Wrap }, IsChecked = true, Margin = new(0, 12, 0, 0) }; options.Add(includeCode);
        void UpdateNames()
        {
            string name = Export.CleanName(nameBox.Text); var software = target.SelectedIndex switch { 0 => ExportTarget.R, 1 => ExportTarget.Sas, _ => ExportTarget.Generic };
            bool zip = chosen == null || package.SelectedIndex == 0;
            includeCode.IsEnabled = zip && software != ExportTarget.Generic;
            var files = zip ? Export.BundleFiles(chosen == null ? Export.ShapeName(name, "wide") : name, software, includeCode.IsEnabled && includeCode.IsChecked == true) : [name + ".csv"];
            nameHint.Text = (zip ? name + ".zip contains " : "Saves ") + string.Join(", ", files) + (chosen == null ? " (and the same for " + Export.ShapeName(name, "long") + ")" : "");
        }
        nameBox.TextChanged += (_, _) => UpdateNames(); target.SelectionChanged += (_, _) => UpdateNames(); package.SelectionChanged += (_, _) => UpdateNames(); includeCode.Checked += (_, _) => UpdateNames(); includeCode.Unchecked += (_, _) => UpdateNames(); refresh = UpdateNames; UpdateNames();
        options.Description("Missing values follow the chosen software. Each layout uses its own sorting and column preferences; Search rows and filter rules apply to each exported layout.");
        if (options.Finish("Continue…") != true) return;
        string datasetName = Export.CleanName(nameBox.Text); lastExportName = datasetName;
        try {
            var exports = new Dictionary<string, View>(); var plans = new Dictionary<string, CodePlan>();
            ExportTarget software = target.SelectedIndex switch { 0 => ExportTarget.R, 1 => ExportTarget.Sas, _ => ExportTarget.Generic };
            bool zip = chosen == null || package.SelectedIndex == 0;
            bool wantCode = zip && software != ExportTarget.Generic && includeCode.IsChecked == true;
            string? codeProblem = null;
            foreach (var shape in chosen == null ? new[] { Layout.Person, Layout.PersonTime } : new[] { chosen.Value }) {
                string context = shape == Layout.Source ? "Source:" + (selectedSource ?? session.Sources.FirstOrDefault())?.Key : shape + ":" + CombineMode.SelectedIndex;
                var prefs = preferences.TryGetValue(context, out var saved) ? saved.Copy() : new ViewPreferences();
                var raw = shape == Layout.Source ? Engine.SourceView(selectedSource ?? session.Sources[0]) : Engine.Build(session, shape, (MatchMode)MatchRule.SelectedIndex, CombineMode.SelectedIndex == 1, prefs.RowVariables);
                string key = chosen == null ? ShapeName(shape).ToLowerInvariant() : "";
                exports.Add(key, Presentation.Apply(Filtering.Apply(raw,structuredFilter),prefs.Copy(),RowFilter.Text));
                if (wantCode)
                {
                    try { plans.Add(key, Recipe.Plan(session, shape, (MatchMode)MatchRule.SelectedIndex, CombineMode.SelectedIndex == 1, (selectedSource ?? session.Sources[0]).Key, prefs, RowFilter.Text, structuredFilter)); }
                    catch (InvalidOperationException ex) { codeProblem ??= ex.Message; }
                }
            }
            Dictionary<string, string> Code(string name)
            {
                var result = new Dictionary<string, string>();
                foreach (var (key, plan) in plans)
                {
                    try { result[key] = software == ExportTarget.R ? Recipe.R(plan, false, Export.ShapeName(name, key)) : SasRecipe.Generate(plan, false, Export.ShapeName(name, key)); }
                    catch (InvalidOperationException ex) { codeProblem ??= ex.Message; }
                }
                return result;
            }
            var cleaning = Code(datasetName);
            if (!ReviewExports(exports, chosen, software, zip, datasetName, cleaning, wantCode ? codeProblem : null)) return;
            var picker = new SaveFileDialog { Title = "Export " + (chosen == null ? "wide and long datasets" : ShapeName(chosen.Value)), FileName = datasetName, Filter = zip ? "Dataset, dictionary and scripts (ZIP)|*.zip" : "CSV dataset|*.csv", AddExtension = true, OverwritePrompt = true };
            if (picker.ShowDialog(this) != true) return;
            if (session.Sources.Any(s => s.ImportPath != null && string.Equals(Path.GetFullPath(picker.FileName), s.ImportPath, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(this, "Choose a different filename. Imported originals cannot be overwritten."); return; }
            // The file name chosen in the save dialog names the dataset and every file inside the ZIP.
            string finalName = Export.CleanName(Path.GetFileNameWithoutExtension(picker.FileName));
            if (finalName != datasetName) { datasetName = finalName; lastExportName = finalName; cleaning = Code(finalName); }
            if (zip) Export.SaveBundles(picker.FileName, exports, software, datasetName, cleaning.Count > 0 ? cleaning : null); else Export.SaveCsv(picker.FileName, exports.Values.Single(), software);
            Status.Text = "Exported " + datasetName + (chosen == null ? " (wide and long)" : " · " + ShapeName(chosen.Value)) + (cleaning.Count > 0 ? " with cleaning code" : "") + " · preview unchanged";
            RecordActivity("Export", Status.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OutOfMemoryException) { MessageBox.Show(this, ex is OutOfMemoryException ? "Not enough memory to prepare this export. Select fewer columns or export one layout at a time." : ex.Message, "Export not completed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session.Sources.Count == 0) return;
        if (MessageBox.Show(this, "Discard this dataset and undo history? No files will be deleted. Export first to keep your changes.", "Clear session", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ++generation; session = new(); undo.Clear(); redo.Clear(); preferences.Clear(); ClearHistory(); structuredFilter=new(); lastSearch=""; selectedSource = null; RowFilter.Clear(); filterTimer.Stop(); rawCache.Clear(); pageStart = 0; VariableSearch.Clear(); SortSearch.Clear(); ExampleLabel.Visibility = Visibility.Collapsed; RenderSources(); _ = RefreshPreview(); Status.Text = "Session cleared";
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (session.Sources.Count > 0 && !Title.Contains("synthetic verification", StringComparison.Ordinal) && MessageBox.Show(this, "Close and discard this session? Export first to keep your changes. Nothing saves automatically.", "Close WideLong", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
        ++generation; session = new(); undo.Clear(); redo.Clear(); preferences.Clear(); view = new(); rawCache.Clear();
    }
}

internal sealed class FormDialog : Window
{
    private readonly StackPanel body = new() { Margin = new(24) };
    public FormDialog(Window owner, string title, string description)
    {
        Owner = owner; Title = title; Width = Math.Min(510,SystemParameters.WorkArea.Width-30); MaxHeight = Math.Min(760,Math.Max(400,SystemParameters.WorkArea.Height-30)); SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Content = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        body.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) }); Description(description);
    }
    public void Add(UIElement element) => body.Children.Add(element);
    public void Description(string text) => body.Children.Add(new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(120, 108, 131)), Margin = new(0, 6, 0, 10), FontSize = 12 });
    private void Label(string text) => body.Children.Add(new TextBlock { Text = text, Margin = new(0, 12, 0, 4), FontWeight = FontWeights.SemiBold });
    public TextBox Input(string label, string value = "") { Label(label); var input = new TextBox { Text = value }; body.Children.Add(input); return input; }
    public ComboBox Choice(string label, string[] options, int selected) { Label(label); var combo = new ComboBox { ItemsSource = options, SelectedIndex = selected }; body.Children.Add(combo); return combo; }
    public bool? Finish(string action, bool allowCancel=true, string cancelText="Cancel")
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        var cancel = new Button { Content = cancelText, IsCancel = true }; var apply = new Button { Content = action, IsDefault = true, Style = (Style)FindResource("Primary") };
        apply.Click += (_, _) => DialogResult = true; if(allowCancel) buttons.Children.Add(cancel); buttons.Children.Add(apply); body.Children.Add(buttons); return ShowDialog();
    }
}
