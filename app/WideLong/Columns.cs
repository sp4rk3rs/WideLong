using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WideLong;

internal sealed record ColumnHeaderLabel(string Title, string Subtitle);

public partial class MainWindow
{
    private DataGridColumnHeader? pressedHeader;
    private Point headerStart;
    private bool headerDragged;
    private Point chipStart;
    private const string SortDragFormat = "WideLong.SortPriority";

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null)
        {
            if (element is T result) return result;
            element = element is Visual || element is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
    private void HeaderDown(object sender, MouseButtonEventArgs e)
    {
        CloseHover(); pressedHeader = null; headerDragged = false;
        if (busy || Ancestor<Thumb>(e.OriginalSource as DependencyObject) != null) return;
        var header = Ancestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column == null || !columnKeys.ContainsKey(header.Column)) return;
        pressedHeader = header; headerStart = e.GetPosition(Preview);
    }
    private void HeaderMove(object sender, MouseEventArgs e)
    {
        if (pressedHeader == null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(Preview);
        if (Math.Abs(point.X - headerStart.X) > SystemParameters.MinimumHorizontalDragDistance || Math.Abs(point.Y - headerStart.Y) > SystemParameters.MinimumVerticalDragDistance) headerDragged = true;
    }
    private async void HeaderUp(object sender, MouseButtonEventArgs e)
    {
        var original = pressedHeader; pressedHeader = null;
        // A column drag finishes only when the captured header receives this mouse-up, so never take capture away
        // while the grid itself reports a drag. Our own flag missed drags that began while a preview was refreshing
        // (the press was ignored as busy, yet the grid still started dragging); releasing capture then left the
        // floating header and drop line on screen. HeaderUpDone cleans up after the grid has handled the release.
        if (headerDragged || GridHeaderDragging()) return;
        var released = Ancestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        // A click is marked handled below, so the header never runs its own mouse-up and never releases capture.
        // A header that kept capture received every later click in the window, so release it.
        if (original != null && original.IsMouseCaptured) original.ReleaseMouseCapture();
        if (busy || original?.Column == null || released?.Column != original.Column || !columnKeys.TryGetValue(original.Column, out string? key)) return;
        e.Handled = true; RecoverHeaderDrag(); await ToggleSort(key);
    }
    /// <summary>Runs after the grid's own mouse-up (including handled ones); cleanup waits until the grid has finished any drag.</summary>
    private void HeaderUpDone(object sender, MouseButtonEventArgs e) => Dispatcher.BeginInvoke(RecoverHeaderDrag, DispatcherPriority.Input);

    // WPF keeps the floating header and drop line as private children of the header row. If a drag is interrupted
    // (capture taken away after the button is released, headers rebuilt mid-drag) they are never removed and survive
    // new data and Clear session, because the header row itself is reused. These members let us detect and clear that state.
    private static readonly System.Reflection.FieldInfo? DragPreparedField = typeof(DataGridColumnHeadersPresenter).GetField("_prepareColumnHeaderDragging", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.FieldInfo? DragActiveField = typeof(DataGridColumnHeadersPresenter).GetField("_isColumnHeaderDragging", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.FieldInfo? DragImageField = typeof(DataGridColumnHeadersPresenter).GetField("_columnHeaderDragIndicator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.FieldInfo? DropLineField = typeof(DataGridColumnHeadersPresenter).GetField("_columnHeaderDropLocationIndicator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
    private static readonly System.Reflection.MethodInfo? ClearDragMethod = typeof(DataGridColumnHeadersPresenter).GetMethod("ClearColumnHeaderDragInfo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes);
    private DataGridColumnHeadersPresenter? HeaderRow()
    {
        var pending = new Stack<DependencyObject>(); pending.Push(Preview);
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            if (item is DataGridColumnHeadersPresenter row) return row;
            for (int i = VisualTreeHelper.GetChildrenCount(item) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(item, i));
        }
        return null;
    }
    private bool GridHeaderDragging() => HeaderRow() is { } row && DragActiveField?.GetValue(row) is true;
    /// <summary>Removes a stranded drag image once no mouse button is held. Safe to call at any time.</summary>
    private void RecoverHeaderDrag()
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed) return;
        // A header click that we handle (sorting) never reaches the grid's own mouse-up, so the grid stays "ready to drag".
        // Moving over the header row later with the button held (for example while selecting cells) then started a drag
        // without mouse capture, and releasing outside a header left the floating header and drop line behind for good.
        if (HeaderRow() is { } row && (DragPreparedField?.GetValue(row) is true || DragActiveField?.GetValue(row) is true || DragImageField?.GetValue(row) != null || DropLineField?.GetValue(row) != null))
        {
            ClearDragMethod?.Invoke(row, null); row.InvalidateMeasure(); row.InvalidateArrange();
        }
        if (Mouse.Captured is DataGridColumnHeader stuck) stuck.ReleaseMouseCapture();
        headerDragged = false; pressedHeader = null;
    }
    private async void HeaderKey(object sender, KeyEventArgs e)
    {
        if (busy || e.Handled || e.Key is not (Key.Space or Key.Enter)) return;
        var header = Ancestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
        if (header?.Column == null || !columnKeys.TryGetValue(header.Column, out string? key)) return;
        e.Handled = true; await ToggleSort(key);
    }
    private async Task ToggleSort(string key)
    {
        if (!await CommitPendingEdit()) return;
        Preferences.ToggleSort(key); Preferences.MoveSortsToFront(); Status.Text = "Sorting updated · click another column for the next priority · click again to remove";
        RecordActivity("View", "Changed sorting for " + (view.Fields.FirstOrDefault(f => f.Key == key)?.Name ?? key));
        await RefreshPreview(rebuild: false, resetPage: true);
        // The newly sorted column has moved to the far left; show it.
        if (Preferences.Sorts.Any(s => s.Key == key) && Preview.Items.Count > 0 && Preview.Columns.Count > 0) Preview.ScrollIntoView(Preview.Items[0], Preview.Columns.OrderBy(c => c.DisplayIndex).First());
    }
    private void Preview_ColumnReordered(object? sender, DataGridColumnEventArgs e)
    {
        if (!ready || buildingColumns || busy) return;
        headerDragged = true; pressedHeader = null;
        var order = Preview.Columns.OrderBy(c => c.DisplayIndex).Select(c => columnKeys[c]).ToArray();
        bool movedSort = Preferences.Sorts.Any(s => s.Key == columnKeys[e.Column]);
        Preferences.ReorderColumns(order, columnKeys[e.Column]);
        Status.Text = movedSort ? "Column order updated · sorted columns now follow their left-to-right priority" : "Column moved · sorting priority unchanged";
        RecordActivity("View", Status.Text);
        Dispatcher.BeginInvoke(async () => await RefreshPreview(rebuild: false, resetPage: movedSort), DispatcherPriority.Background);
    }
    private async void ClearSort_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !await CommitPendingEdit()) return;
        Preferences.Sorts.Clear(); RecordActivity("View", "Cleared sorting"); await RefreshPreview(rebuild: false, resetPage: true); Status.Text = "Sorting cleared · source row order restored";
    }

    private void DecorateColumn(DataGridColumn column, OutputField field, List<SortRule> active)
    {
        int rank = active.FindIndex(s => s.Key == field.Key);
        var sort = rank < 0 ? null : active[rank];
        // Headers are recycled during horizontal scrolling. Store label data rather than a UIElement:
        // a reused ContentPresenter can otherwise leave the old visual detached and the header blank.
        column.HeaderTemplate = (DataTemplate)FindResource("VariableHeaderTemplate");
        column.Header = new ColumnHeaderLabel(
            (sort == null ? "" : (rank + 1) + "  ") + field.Name + (sort == null ? "" : sort.Descending ? "  ↓" : "  ↑"),
            field.Source + " · " + (field.Kind == ValueKind.Number ? "number" : field.Kind.ToString().ToLowerInvariant()));
        var style = new Style(typeof(DataGridColumnHeader), (Style)FindResource(typeof(DataGridColumnHeader)));
        if (sort != null) { style.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("Accent"))); style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White)); }
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, "Click to add/remove sorting. Drag to move this column. Right-click for actions."));
        style.Setters.Add(new Setter(ToolTipService.IsEnabledProperty, false));
        style.Setters.Add(new EventSetter(MouseEnterEvent, new MouseEventHandler((sender, _) => ScheduleHover((FrameworkElement)sender))));
        style.Setters.Add(new EventSetter(MouseLeaveEvent, new MouseEventHandler((_, _) => LeaveHover())));
        // Menus are built on demand for whichever column the (possibly recycled) header currently shows.
        style.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, PlaceholderMenu));
        style.Setters.Add(new EventSetter(ContextMenuOpeningEvent, new ContextMenuEventHandler((sender, _) => { if (sender is DataGridColumnHeader { Column: { } c } header && columnFields.TryGetValue(c, out var current)) header.ContextMenu = OutputMenu(current); })));
        style.Setters.Add(new Setter(AutomationProperties.NameProperty, field.Name + (sort == null ? " — click to sort" : " — sort priority " + (rank + 1) + (sort.Descending ? " descending" : " ascending"))));
        column.HeaderStyle = style;
        if (sort != null)
        {
            var cell = new Style(typeof(DataGridCell), (Style)FindResource(typeof(DataGridCell)));
            cell.Setters.Add(new Setter(Control.BackgroundProperty, FindResource("SortedCell"))); column.CellStyle = cell;
        }
        else column.CellStyle = null;
    }

    private void RenderSortChips()
    {
        SortChips.Children.Clear();
        var active = Preferences.Sorts.Where(rule => view.Fields.Any(f => f.Key == rule.Key)).ToList();
        if (active.Count == 0)
        {
            SortChips.Children.Add(new TextBlock { Text = "Click a column name to start sorting", Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new(0, 7, 0, 0) }); return;
        }
        for (int i = 0; i < active.Count; i++)
        {
            var rule = active[i]; var field = view.Fields.Single(f => f.Key == rule.Key);
            var chip = new Border { Background = (Brush)FindResource("Accent"), CornerRadius = new(5), Margin = new(0, 0, 7, 3), Padding = new(8, 3, 4, 3), AllowDrop = true, Cursor = Cursors.SizeAll, ToolTip = "Drag to change sorting priority" };
            var body = new StackPanel { Orientation = Orientation.Horizontal };
            body.Children.Add(new TextBlock { Text = (i + 1) + "  " + field.Name, Foreground = Brushes.White, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0), TextWrapping = TextWrapping.NoWrap });
            var direction = new Button { Content = rule.Descending ? "↓" : "↑", ToolTip = "Reverse sort direction", Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(6, 3, 6, 3), Margin = new(0), FontSize = 12 };
            direction.Click += async (_, _) => { if (busy) return; int index = Preferences.Sorts.FindIndex(s => s.Key == rule.Key); if (index >= 0) Preferences.Sorts[index] = rule with { Descending = !rule.Descending }; RecordActivity("View", "Reversed sorting for " + field.Name); await RefreshPreview(rebuild: false, resetPage: true); };
            var remove = new Button { Content = "×", ToolTip = "Remove this sorting variable", Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(6, 3, 6, 3), Margin = new(0), FontSize = 12 };
            remove.Click += async (_, _) => { if (busy) return; Preferences.Sorts.RemoveAll(s => s.Key == rule.Key); RecordActivity("View", "Removed sorting for " + field.Name); await RefreshPreview(rebuild: false, resetPage: true); };
            body.Children.Add(direction); body.Children.Add(remove); chip.Child = body;
            chip.PreviewMouseLeftButtonDown += (_, e) => chipStart = e.GetPosition(chip);
            chip.MouseMove += (_, e) =>
            {
                if (busy || e.LeftButton != MouseButtonState.Pressed || Ancestor<Button>(e.OriginalSource as DependencyObject) != null) return;
                var position = e.GetPosition(chip);
                if (Math.Abs(position.X - chipStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - chipStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                DragDrop.DoDragDrop(chip, new DataObject(SortDragFormat, rule.Key), DragDropEffects.Move); e.Handled = true;
            };
            chip.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(SortDragFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            chip.Drop += async (_, e) =>
            {
                if (busy || e.Data.GetData(SortDragFormat) is not string from) return;
                Preferences.MoveSort(from, rule.Key); Preferences.MoveSortsToFront(); RecordActivity("View", "Changed sorting priority"); e.Handled = true; await RefreshPreview(rebuild: false, resetPage: true); Status.Text = "Sorting priority updated";
            };
            SortChips.Children.Add(chip);
        }
    }

    private static readonly Brush DisabledMenuText = new SolidColorBrush(Color.FromRgb(0xA8, 0xA1, 0xB0));
    /// <summary>Unavailable actions are visibly greyed and explain why on hover.</summary>
    private static MenuItem Menu(string title, Action action, bool enabled = true, string? whyUnavailable = null)
    {
        var text = new TextBlock { Text = title };
        if (!enabled) { text.Foreground = DisabledMenuText; text.FontStyle = FontStyles.Italic; }
        var item = new MenuItem { Header = text, IsEnabled = enabled };
        if (!enabled && whyUnavailable != null) { item.ToolTip = whyUnavailable; ToolTipService.SetShowOnDisabled(item, true); }
        item.Click += (_, _) => action(); return item;
    }
    private (Source Source, int Column)? ResolveField(OutputField field)
    {
        foreach (var source in session.Sources)
        {
            string prefix = source.Key + ":";
            if (!field.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var parts = field.Key[prefix.Length..].Split("\u001ftime:", StringSplitOptions.None);
            string key = parts[0].Split("\u001fentity:", StringSplitOptions.None)[0];
            if (int.TryParse(key, out int column) && column >= 0 && column < source.Fields.Count) return (source, column);
            if (key.StartsWith("measure:", StringComparison.Ordinal))
            {
                int c = source.Fields.FindIndex(f => f.Measure == key[8..] && (parts.Length==1 || f.Occasion==parts[1])); if (c >= 0) return (source, c);
            }
        }
        return null;
    }
    private ContextMenu OutputMenu(OutputField field)
    {
        var resolved = ResolveField(field);
        if (resolved is { } derived && derived.Source.IsDerived(derived.Column) && !field.Key.EndsWith(Engine.PartnerSuffix, StringComparison.Ordinal))
        {
            var scaleMenu = DerivedMenu(derived.Source, derived.Column);
            scaleMenu.Items.Insert(0, Menu("Rename this output column…", () => RenameOutput(field)));
            return scaleMenu;
        }
        var menu = new ContextMenu();
        menu.Items.Add(Menu("Rename this output column…", () => RenameOutput(field)));
        bool keyField = field.Key is "@person_id" or "@entity" or "@time" && KeyTargets(field.Key).Count > 0;
        string unavailable = field.Key.StartsWith('@') ? "Matching keys are edited in each file: use View file, or ID / grouping." : "This column is generated by the layout and has no single source variable.";
        menu.Items.Add(Menu("Change variable type…", () => { if (resolved is { } x) ChangeType(x.Source, x.Column); else if (keyField) ChangeKeyType(field.Key); }, resolved != null || keyField, unavailable));
        menu.Items.Add(Menu("Replace values…", () => { if (resolved is { } x) ReplaceValues(x.Source, x.Column); }, resolved != null, unavailable));
        menu.Items.Add(Menu("Missing-value rules…", () => { if (resolved is { } x) MissingRules(x.Source, x.Column, field.Key.Contains(":measure:", StringComparison.Ordinal)); }, resolved != null, field.Key.StartsWith('@') ? "Matching keys can never be missing." : unavailable));
        var revert = resolved is { } r0 ? RevertTargets(r0.Source, r0.Column) : keyField ? KeyTargets(field.Key).Where(t => HasTypeChange(t.Source, t.Column)).ToList() : [];
        menu.Items.Add(Menu("Revert variable type", () => { if (revert.Count > 0) RevertType(revert[0].Source, revert[0].Column); }, revert.Count > 0, "This variable has no recorded type change. Values are still the original text."));
        var responses = ResponseTargets(field);
        menu.Items.Add(Menu(responses.Targets is { } rt && rt.Any(t => t.Source.KeepRules.Any(k => k.Column == t.Column)) ? "Filter responses… (active)" : "Filter responses…", () => { if (responses.Targets is { } t) FilterResponses(t); }, responses.Targets != null, responses.Why));
        bool timed = field.Key == "@time" || field.Key.Contains("\u001ftime:", StringComparison.Ordinal);
        if (timed) menu.Items.Add(Menu("Order time values…", OrderTimeValues, KeyTargets("@time").Count > 0 || session.Sources.Any(s => s.Fields.Any(f => f.Measure.Length > 0)), "No file has a time / grouping column or repeated columns."));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("Sort ascending", () => SetDirection(field.Key, false)));
        menu.Items.Add(Menu("Sort descending", () => SetDirection(field.Key, true)));
        if (Preferences.Sorts.Any(s => s.Key == field.Key))
            menu.Items.Add(Menu("Remove from sorting", async () => { if (busy) return; Preferences.Sorts.RemoveAll(s => s.Key == field.Key); RecordActivity("View", "Removed sorting for " + field.Name); await RefreshPreview(rebuild: false, resetPage: true); }));
        else menu.Items.Add(Menu("Add to sorting", () => SetDirection(field.Key, false)));
        if (resolved is { } sourceField)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Menu("Value labels…", () => ValueLabels(sourceField.Source, sourceField.Column)));
            menu.Items.Add(Menu("New scale score…", () => ScaleDialog(sourceField.Source)));
            menu.Items.Add(Menu("Repeated column settings…", () => RepeatedSettings(sourceField.Source, sourceField.Column)));
            menu.Items.Add(Menu("Map this file's repeated columns…", () => OpenMappingEditor(sourceField.Source)));
        }
        return menu;
    }
    private ContextMenu SourceMenu(Source source, int column)
    {
        if (source.IsDerived(column)) return DerivedMenu(source, column);
        var menu = new ContextMenu();
        menu.Items.Add(Menu("Rename source column…", () => RenameSource(source, column)));
        menu.Items.Add(Menu("Change variable type…", () => ChangeType(source, column)));
        menu.Items.Add(Menu("Replace values…", () => ReplaceValues(source, column)));
        menu.Items.Add(Menu("Missing-value rules…", () => MissingRules(source, column)));
        var revert = RevertTargets(source, column);
        menu.Items.Add(Menu("Revert variable type", () => RevertType(source, column), revert.Count > 0, "This variable has no recorded type change. Values are still the original text."));
        var responses = ResponseTargets(source, column);
        menu.Items.Add(Menu(source.KeepRules.Any(k => k.Column == column) ? "Filter responses… (active)" : "Filter responses…", () => { if (responses.Targets is { } t) FilterResponses(t); }, responses.Targets != null, responses.Why));
        menu.Items.Add(new Separator());
        menu.Items.Add(Menu("Value labels…", () => ValueLabels(source, column)));
        menu.Items.Add(Menu("New scale score…", () => ScaleDialog(source)));
        menu.Items.Add(Menu("Repeated column settings…", () => RepeatedSettings(source, column)));
        menu.Items.Add(Menu("Map this file's repeated columns…", () => OpenMappingEditor(source)));
        return menu;
    }
    private async void SetDirection(string key, bool descending)
    {
        if (busy || !await CommitPendingEdit()) return;
        int index = Preferences.Sorts.FindIndex(s => s.Key == key);
        if (index < 0) Preferences.Sorts.Add(new(key, descending)); else Preferences.Sorts[index] = new(key, descending);
        Preferences.MoveSortsToFront();
        RecordActivity("View", "Sorted " + (view.Fields.FirstOrDefault(f => f.Key == key)?.Name ?? key) + (descending ? " descending" : " ascending"));
        await RefreshPreview(rebuild: false, resetPage: true);
    }
    private async void RenameOutput(OutputField field)
    {
        if (busy) return;
        var d = new FormDialog(this, "Rename column", "This name will appear in the current layout and its export."); var name = d.Input("Column name", field.Name);
        if (d.Finish("Rename") != true) return;
        string value = name.Text.Trim();
        if (value.Length == 0 || view.Fields.Any(f => f.Key != field.Key && f.Name.Equals(value, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(this, "Choose a nonempty name that is not already used in this output."); return; }
        Preferences.Names[field.Key] = value; RecordActivity("View", "Renamed output column " + field.Name + " to " + value); await RefreshPreview(rebuild: false); Status.Text = "Output column renamed";
    }
    private void RenameSource(Source source, int column)
    {
        if (busy) return;
        var d = new FormDialog(this, "Rename source column", source.Name); var name = d.Input("Column name", source.Fields[column].Name);
        if (d.Finish("Rename") != true) return;
        string value = name.Text.Trim();
        if (value.Length == 0 || source.Fields.Where((_, i) => i != column).Any(f => f.Name.Equals(value, StringComparison.OrdinalIgnoreCase))) { MessageBox.Show(this, "Choose a nonempty, unique name."); return; }
        string oldName = source.Fields[column].Name;
        Change(() => source.Fields[column].Name = value, "Renamed " + source.Name + " / " + oldName + " to " + value);
    }

    private static string KeyRole(string key) => key switch { "@person_id" => "ID", "@entity" => "additional key", _ => "time / grouping" };
    private static int KeyColumn(Source source, string key) => key switch { "@person_id" => source.IdColumn, "@entity" => source.EntityColumn, _ => source.TimeColumn };
    private static string? KeyFor(Source source, int column) => column < 0 ? null : column == source.IdColumn ? "@person_id" : column == source.EntityColumn ? "@entity" : column == source.TimeColumn ? "@time" : null;
    private List<(Source Source, int Column)> KeyTargets(string key) => session.Sources.Select(s => (Source: s, Column: KeyColumn(s, key))).Where(x => x.Column >= 0 && x.Column < x.Source.Fields.Count).ToList();
    private void ChangeKeyType(string key)
    {
        var targets = KeyTargets(key);
        if (targets.Count == 0) { MessageBox.Show(this, "No file has a " + KeyRole(key) + " column. Choose one in ID / grouping first."); return; }
        ChangeType(targets[0].Source, targets[0].Column);
    }

    private void ChangeType(Source source, int column)
    {
        if (busy) return;
        var field = source.Fields[column];
        string? key = KeyFor(source, column);
        var siblings = key == null ? new List<(Source Source, int Column)>() : KeyTargets(key).Where(t => t.Source != source).ToList();
        var d = new FormDialog(this, "Change type · " + field.Name, "All values are checked before any change is applied.");
        var kind = d.Choice("Treat values as", ["Text", "Number", "Date", "Boolean"], (int)field.Kind);
        var group = new CheckBox { Content = new TextBlock { Text = "Apply to every repeated column of " + field.Measure }, IsChecked = true, Margin = new(0, 12, 0, 5), Visibility = field.Measure.Length > 0 ? Visibility.Visible : Visibility.Collapsed }; d.Add(group);
        // Keys match by exact value across files, so their types should change together.
        var allFiles = new CheckBox { Content = new TextBlock { Text = $"Apply to the {KeyRole(key ?? "")} column in every file ({siblings.Count + 1} files: " + string.Join(", ", siblings.Select(t => t.Source.Name + " / " + t.Source.Fields[t.Column].Name).Prepend(source.Name + " / " + field.Name)) + ")" }, IsChecked = true, Margin = new(0, 12, 0, 5), Visibility = siblings.Count > 0 ? Visibility.Visible : Visibility.Collapsed, ToolTip = "Records only match when the key values are spelled identically. Converting every file together keeps 007 and 7 from silently failing to match." }; d.Add(allFiles);
        if (key != null) d.Description($"The combined {KeyRole(key)} column follows this type when every file uses the same type; otherwise it stays Text. Numeric IDs sort as numbers (2 before 10). Converting to Number removes leading zeros and normalizes numeric text (00001 → 1; 201.0 → 201). Review the examples below before applying. Distinct key values must stay distinct across the selected files. Keep identifiers over 15 digits as Text.");
        var dates = new StackPanel { Margin = new(0, 9, 0, 0) }; dates.Children.Add(new TextBlock { Text = "Current date format (for example MM/dd/yyyy)", FontSize = 12 });
        var format = new TextBox { Text = "yyyy-MM-dd", Margin = new(0, 5, 0, 0) }; dates.Children.Add(format); d.Add(dates);
        var summary = new TextBlock { Margin = new(0, 14, 0, 0), FontSize = 12 }; d.Add(summary);
        var examples = new TextBlock { FontSize = 12, Margin = new(0, 10, 0, 0) }; d.Add(new ScrollViewer { Content = examples, MaxHeight = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        List<(Source Source, List<int> Columns)> Targets()
        {
            var own = group.IsChecked == true && field.Measure.Length > 0 ? source.Fields.Select((f, i) => (f, i)).Where(x => x.f.Measure == field.Measure).Select(x => x.i).ToList() : new List<int> { column };
            var result = new List<(Source, List<int>)> { (source, own) };
            if (allFiles.IsChecked == true) result.AddRange(siblings.Select(t => (t.Source, new List<int> { t.Column })));
            return result;
        }
        // Conversions may normalize keys, but must not merge distinct values within or across files.
        int Collisions(ValueKind type) => key == null ? 0 : Values.KeyConversionCollisions(
            Targets().SelectMany(t => t.Columns.Select(c => (t.Source, c))), type, format.Text);
        void Update()
        {
            var type = (ValueKind)kind.SelectedIndex; dates.Visibility = type == ValueKind.Date ? Visibility.Visible : Visibility.Collapsed;
            if (type == ValueKind.Date && Values.DateFormatProblem(format.Text) is { } problem) { summary.Text = problem + " Nothing will change."; summary.Foreground = Brushes.DarkRed; examples.Text = ""; return; }
            var previews = Targets().Select(t => (t.Source, Preview: Values.PreviewConversion(t.Source, t.Columns, type, format.Text))).ToList();
            int total = previews.Sum(p => p.Preview.Total), changed = previews.Sum(p => p.Preview.Changed), missingAfter = previews.Sum(p => p.Preview.Missing), newly = previews.Sum(p => p.Preview.NewlyMissing), zeros = previews.Sum(p => p.Preview.LeadingZeroLoss), invalid = previews.Sum(p => p.Preview.Invalid);
            int collisions = Collisions(type);
            int longNumbers = type == ValueKind.Number ? Targets().Sum(t => t.Columns.Sum(c => t.Source.Rows.Count(r => r[c].Count(char.IsDigit) > 15))) : 0;
            summary.Text = $"{total:N0} values checked · {changed:N0} values change · {missingAfter:N0} missing after conversion ({newly:N0} newly missing).\n" + (invalid == 0 ? "All values can convert." : $"{invalid:N0} invalid values: resolve these before applying. Nothing will change.") + (zeros > 0 ? $"\n{zeros:N0} values lose leading zeros when changed to Number. Keep identifiers as Text if those zeros matter." : "") + (collisions > 0 ? $"\n{collisions:N0} different {KeyRole(key ?? "")} values would become identical (for example 007 and 7). This would merge different records, so it cannot be applied." : "") + (longNumbers > 0 ? $"\n{longNumbers:N0} values have more than 15 digits; R/SAS code export cannot reproduce them exactly as numbers." : "");
            string prefix(Source s) => previews.Count > 1 ? s.Name + " / " : "";
            examples.Text = "Examples (data row numbers, not counting the header — the same numbering as Review matching and the cleaning code):\n" + string.Join("\n", previews.SelectMany(p => p.Preview.Examples.Select(x => (p.Source, x))).Take(10).Select(e => prefix(e.Source) + e.x.Variable + ", row " + e.x.Record + ": " + PreviewValue(e.x.Before) + " → " + (e.x.Invalid ? "Cannot convert" : e.x.Missing ? "Missing" : PreviewValue(e.x.After))));
            summary.Foreground = invalid == 0 && collisions == 0 ? (Brush)FindResource("Accent") : Brushes.DarkRed;
        }
        kind.SelectionChanged += (_, _) => Update(); group.Checked += (_, _) => Update(); group.Unchecked += (_, _) => Update(); allFiles.Checked += (_, _) => Update(); allFiles.Unchecked += (_, _) => Update(); format.TextChanged += (_, _) => Update(); Update();
        if (d.Finish("Apply type") != true) return;
        var target = (ValueKind)kind.SelectedIndex; var selected = Targets();
        if (selected.Any(t => t.Columns.Any(c => Values.Failures(t.Source, c, target, format.Text).Count > 0))) { MessageBox.Show(this, "Some values cannot convert. Correct or replace them, then try again. No changes were applied."); return; }
        if (key != null && target != ValueKind.Text && selected.Any(t => t.Columns.Any(c => t.Source.EffectiveMissingTokens(c).Any(token => t.Source.Rows.Any(r => r[c] == token && token.Length > 0)))))
        { MessageBox.Show(this, $"Some {KeyRole(key)} values match a missing-value code and would become missing. Recode them first. No changes were applied."); return; }
        if (Collisions(target) > 0) { MessageBox.Show(this, $"Different {KeyRole(key ?? "")} values would become identical after conversion (for example 007 and 7), merging different records. Keep this column as Text or recode the values first. No changes were applied.", "Change type", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var converted = selected.SelectMany(t => t.Columns.Select(c => (t.Source, Column: c, Values: t.Source.Rows.Select(r => Values.ConvertWithMissing(r[c], target, format.Text, t.Source.EffectiveMissingTokens(c))).ToArray()))).ToList();
        int columnCount = converted.Count;
        string description = selected.Count > 1 ? $"{KeyRole(key ?? "")} in {selected.Count} files" : source.Name + " / " + (columnCount == 1 ? field.Name : field.Measure + " (" + columnCount + " columns)");
        Change(() =>
        {
            foreach (var item in converted)
            {
                var s = item.Source; s.CaptureOriginal();
                var tokens = s.EffectiveMissingTokens(item.Column).ToArray(); string pattern = format.Text;
                s.Steps.Add(new(StepKind.Convert, item.Column, Target: target, DateFormat: pattern, MissingTokens: tokens));
                RemapKeepRule(s, item.Column, v => Values.ConvertWithMissing(v, target, pattern, tokens));
                s.Fields[item.Column].Kind = target;
                for (int r = 0; r < s.Rows.Count; r++) s.Rows[r][item.Column] = item.Values[r];
            }
        }, "Converted " + description + " to " + target);
    }
    private void ReplaceValues(Source source, int column)
    {
        if (busy) return;
        var field = source.Fields[column]; var d = new FormDialog(this, "Replace values · " + field.Name, "Matches exact values in this source variable. Leave a box empty to use a blank value.");
        var find = d.Input("Find"); var replacement = d.Input("Replace with"); var summary = new TextBlock { Margin = new(0, 13, 0, 0), Foreground = (Brush)FindResource("Accent") }; d.Add(summary);
        void Update() => summary.Text = $"{source.Rows.Count(r => r[column] == find.Text):N0} matching values will be replaced.";
        find.TextChanged += (_, _) => Update(); Update(); if (d.Finish("Replace matches") != true) return;
        string value; try { value = Values.ConvertWithMissing(replacement.Text, field.Kind, "yyyy-MM-dd", source.EffectiveMissingTokens(column)); } catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message); return; }
        var matches = source.Rows.Where(r => r[column] == find.Text).ToList();
        if (matches.Count == 0) { MessageBox.Show(this, "No exact matches. Nothing changed."); return; }
        string before = find.Text;
        bool filtered = source.KeepRules.Any(r => r.Column == column);
        Change(() => { source.CaptureOriginal(); source.Steps.Add(new(StepKind.Replace, column, Before: before, After: value)); foreach (var row in matches) row[column] = value; ResponseFilters.FollowReplace(source, column, before, value); },
            $"Replaced {matches.Count:N0} values in {source.Name} / {field.Name}: {PreviewValue(before)} → {PreviewValue(value)}" + (filtered ? " (response filter updated to keep the same records)" : ""));
    }
    /// <summary>Sets the order of time/grouping values (Baseline → Month 3 → Month 6). It decides the order of wide columns;
    /// values not placed keep the order in which they occur in the data.</summary>
    private void OrderTimeValues()
    {
        if (busy) return;
        List<string> values;
        try { values = Engine.TimeValues(session, (MatchMode)MatchRule.SelectedIndex); }
        catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message, "Order time values"); return; }
        if (values.Count < 2) { MessageBox.Show(this, "There are fewer than two time / grouping values to order.", "Order time values"); return; }
        var d = new FormDialog(this, "Order time values", "Wide columns follow this order (for example Baseline → Month 3 → Month 6). The order is also used by the cleaning code. It is part of this session only.");
        var list = new ListBox { ItemsSource = values.ToList(), Height = Math.Min(320, 26 * values.Count + 10), Margin = new(0, 6, 0, 6), SelectedIndex = 0 };
        var items = values.ToList();
        void Move(int delta)
        {
            int i = list.SelectedIndex; int j = i + delta; if (i < 0 || j < 0 || j >= items.Count) return;
            (items[i], items[j]) = (items[j], items[i]); list.ItemsSource = null; list.ItemsSource = items.ToList(); list.SelectedIndex = j;
        }
        var buttons = new WrapPanel(); var up = SmallButton("Move up"); var down = SmallButton("Move down"); var natural = SmallButton("Data order");
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        natural.Click += (_, _) => { var trial = session.Copy(includeOriginals: false); trial.TimeOrder.Clear(); try { items = Engine.TimeValues(trial, (MatchMode)MatchRule.SelectedIndex); list.ItemsSource = items.ToList(); } catch (InvalidOperationException) { } };
        buttons.Children.Add(up); buttons.Children.Add(down); buttons.Children.Add(natural);
        d.Add(list); d.Add(buttons);
        if (d.Finish("Apply order") != true) return;
        var chosen = items.ToList();
        Change(() => session.TimeOrder = chosen, "Ordered time values: " + string.Join(" → ", chosen.Take(6)) + (chosen.Count > 6 ? " → …" : ""));
    }
    private void RepeatedSettings(Source source, int column)
    {
        if (busy) return;
        var field = source.Fields[column]; var d = new FormDialog(this, "Repeated columns", "Use this when several columns contain the same variable for different grouping values (time, term, wave, trial, etc.).");
        d.Description(source.Name + " / " + field.Name);
        var measure = d.Input("Shared variable name (e.g. temperature)", field.Measure);
        var visit = d.Input("Grouping value for this column (e.g. Month 3)", field.Occasion);
        d.Description("Clear both boxes for a regular column. Grouping values must match the other files exactly.");
        if (d.Finish("Apply") != true) return;
        string name = measure.Text.Trim(); if ((name.Length > 0) != (visit.Text.Length > 0)) { MessageBox.Show(this, "Fill both boxes, or clear both."); return; }
        Change(() => { field.Measure = name; field.Occasion = visit.Text; }, "Mapped " + source.Name + " / " + field.Name + " to " + (name.Length == 0 ? "a regular column" : name + " / " + visit.Text));
    }
}
