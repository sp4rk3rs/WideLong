using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WideLong;

public partial class MainWindow
{
    private RowFilterSpec structuredFilter = new();
    private View ApplyStructuredFilters(View input) => Filtering.Apply(input, structuredFilter);
    private string StructuredFilterSummary => Filtering.Summary(structuredFilter);
    private void UpdateFilterSummary() { /* Parent RefreshPreview renders StructuredFilterSummary. */ }
    private void ClearStructuredFiltersForNavigation()
    {
        if (structuredFilter.Rules.Count == 0) return;
        structuredFilter = new(); UpdateFilterSummary(); RecordActivity("Filters", "Cleared filter rules to show a source record");
    }
    private sealed record FilterColumn(OutputField Field, bool Available)
    {
        public override string ToString() => Field.Name + " · " + Field.Kind + (Available ? "" : " (unavailable in this layout)");
    }
    private sealed record OperatorChoice(FilterOperator Operator)
    {
        public override string ToString() => Filtering.Label(Operator);
    }
    private sealed class RuleEditor
    {
        public Grid Panel { get; } = new() { Margin = new(0, 0, 0, 10) };
        public ComboBox Column { get; } = new() { MinWidth = 170 };
        public ComboBox Operator { get; } = new() { MinWidth = 145 };
        public TextBox Value { get; } = new() { MinWidth = 110 };
        public TextBox Upper { get; } = new() { MinWidth = 110 };
        public Button Remove { get; } = new() { Content = "Remove", Padding = new(8, 6, 8, 6), Margin = new(0), FontSize = 11 };
        public RuleEditor(List<FilterColumn> columns, RowFilterRule? rule, Action changed)
        {
            foreach (var width in new[] { new GridLength(2, GridUnitType.Star), new(160), new(1, GridUnitType.Star), new(1, GridUnitType.Star), GridLength.Auto }) Panel.ColumnDefinitions.Add(new() { Width = width });
            Column.ItemsSource = columns;
            Column.SelectedItem = columns.FirstOrDefault(c => c.Field.Key == rule?.Key) ?? columns.FirstOrDefault();
            void Operators(FilterOperator? selected)
            {
                var choices = Filtering.Operators((Column.SelectedItem as FilterColumn)?.Field.Kind ?? ValueKind.Text).Select(op => new OperatorChoice(op)).ToList();
                Operator.ItemsSource = choices; Operator.SelectedItem = choices.FirstOrDefault(c => c.Operator == selected) ?? choices[0];
            }
            void Visibility()
            {
                var op = (Operator.SelectedItem as OperatorChoice)?.Operator ?? FilterOperator.Equal;
                Value.Visibility = Filtering.NeedsValue(op) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
                Upper.Visibility = op == FilterOperator.Between ? System.Windows.Visibility.Visible : System.Windows.Visibility.Hidden;
                var kind = (Column.SelectedItem as FilterColumn)?.Field.Kind ?? ValueKind.Text;
                Value.ToolTip = kind == ValueKind.Date ? "yyyy-MM-dd" : kind == ValueKind.Boolean ? "true / false, yes / no, 1 / 0" : "Rule value (Text is case-sensitive)";
                Upper.ToolTip = kind == ValueKind.Date ? "Upper date, yyyy-MM-dd" : "Upper bound (inclusive)";
            }
            Operators(rule?.Operator); Value.Text = rule?.Value ?? ""; Upper.Text = rule?.UpperValue ?? ""; Visibility();
            Column.SelectionChanged += (_, _) => { Operators((Operator.SelectedItem as OperatorChoice)?.Operator); Visibility(); changed(); };
            Operator.SelectionChanged += (_, _) => { Visibility(); changed(); };
            Value.TextChanged += (_, _) => changed(); Upper.TextChanged += (_, _) => changed();
            UIElement[] controls = [Column, Operator, Value, Upper, Remove];
            for (int i = 0; i < controls.Length; i++) { Grid.SetColumn(controls[i], i); if (controls[i] is FrameworkElement control && i < controls.Length - 1) control.Margin = new(0, 0, 7, 0); Panel.Children.Add(controls[i]); }
        }
        public RowFilterRule Read()
        {
            if (Column.SelectedItem is not FilterColumn column || Operator.SelectedItem is not OperatorChoice choice) throw new InvalidOperationException("Choose a variable and comparison for each rule.");
            return Filtering.Normalize(new(column.Field.Key, column.Field.Name, column.Field.Kind, choice.Operator, Value.Text, Upper.Text));
        }
    }
    private async void FilterRules_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !await CommitPendingEdit()) return;
        CloseHover();
        View available;
        string? sourceProblem = null;
        try
        {
            var raw = layout == Layout.Source ? session.Sources.Count == 0 ? new View() : Engine.SourceView(selectedSource ?? session.Sources[0]) : Engine.Build(session, layout, (MatchMode)MatchRule.SelectedIndex, CombineMode.SelectedIndex == 1, Preferences.RowVariables);
            available = Presentation.Apply(raw, Preferences.Copy());
        }
        catch (InvalidOperationException ex) { available = new(); sourceProblem = ex.Message; }
        // Old rules remain available for inspection/removal even when a new layout cannot expose their columns.
        var columns = available.Fields.Select(f => new FilterColumn(f, true)).ToList();
        foreach (var rule in structuredFilter.Rules)
            if (columns.All(c => c.Field.Key != rule.Key)) columns.Add(new(new(rule.Name, rule.Kind, "filter rule", rule.Key), false));
        var d = new FormDialog(this, "Filter rows", "Choose a variable, comparison, and value for each rule. Missing values match only explicit missing/not-missing rules. Text comparisons match case exactly; Search rows remains a separate case-insensitive text search.") { Width = 1020 };
        if (sourceProblem != null) d.Description("The current dataset cannot be built yet: " + sourceProblem + " You can inspect or clear existing rules here.");
        var mode = d.Choice("Keep rows matching", ["All rules (AND)", "Any rule (OR)"], structuredFilter.MatchAll ? 0 : 1);
        var editors = new List<RuleEditor>(); var list = new StackPanel();
        var commands = new WrapPanel { Margin = new(0, 7, 0, 10) }; var add = SmallButton("+ Add rule"); var clear = SmallButton("Clear rules"); commands.Children.Add(add); commands.Children.Add(clear); d.Add(commands);
        var header = new Grid { Margin=new(0,0,0,7) };
        foreach(var width in new[]{new GridLength(2,GridUnitType.Star),new(160),new(1,GridUnitType.Star),new(1,GridUnitType.Star),new(61)}) header.ColumnDefinitions.Add(new(){Width=width});
        var labels=new[]{"Variable / type","Comparison","Value","Upper bound (Between)"};
        for(int i=0;i<labels.Length;i++){var text=new TextBlock{Text=labels[i],FontSize=11,Foreground=(Brush)FindResource("Muted")};Grid.SetColumn(text,i);header.Children.Add(text);}d.Add(header);
        d.Add(new ScrollViewer { Content = list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var validation = new TextBlock { Margin = new(0, 10, 0, 0), FontSize = 12 }; d.Add(validation);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; var apply = new Button { Content = "Apply filters", IsDefault = true, Style = (Style)FindResource("Primary") }; buttons.Children.Add(cancel); buttons.Children.Add(apply); d.Add(buttons);
        RowFilterSpec Draft() => new() { MatchAll = mode.SelectedIndex == 0, Rules = editors.Select(editor => editor.Read()).ToList() };
        void Update()
        {
            try
            {
                var draft = Draft();
                if (draft.Rules.Count > 0 && sourceProblem != null) throw new InvalidOperationException("Resolve the current dataset problem or clear these rules before applying.");
                var filtered = Filtering.Apply(available, draft);
                validation.Text = draft.Rules.Count == 0 ? "No filter rules. Search rows still applies separately." : $"{filtered.Rows.Count:N0} of {available.Rows.Count:N0} rows match these rules before Search rows.";
                validation.Foreground = (Brush)FindResource("Accent"); apply.IsEnabled = true;
            }
            catch (InvalidOperationException ex) { validation.Text = ex.Message; validation.Foreground = Brushes.DarkRed; apply.IsEnabled = false; }
            add.IsEnabled = columns.Any(c => c.Available);
        }
        void Add(RowFilterRule? rule)
        {
            var editor = new RuleEditor(columns, rule, Update); editors.Add(editor); list.Children.Add(editor.Panel);
            editor.Remove.Click += (_, _) => { editors.Remove(editor); list.Children.Remove(editor.Panel); Update(); }; Update();
        }
        foreach (var rule in structuredFilter.Rules) Add(rule);
        add.Click += (_, _) => Add(null); clear.Click += (_, _) => { editors.Clear(); list.Children.Clear(); Update(); };
        mode.SelectionChanged += (_, _) => Update(); Update();
        apply.Click += (_, _) => { try { Filtering.Compile(available.Fields, Draft()); d.DialogResult = true; } catch (InvalidOperationException ex) { validation.Text = ex.Message; } };
        if (d.ShowDialog() != true) return;
        structuredFilter = Draft(); UpdateFilterSummary();
        RecordActivity("Filters", structuredFilter.Rules.Count==0?"Cleared filter rules":Filtering.Summary(structuredFilter)+": "+string.Join("; ",structuredFilter.Rules.Take(3).Select(r=>r.Name+" "+Filtering.Label(r.Operator)+(Filtering.NeedsValue(r.Operator)?" "+(r.Value.Length>60?r.Value[..60]+"…":r.Value):"")+(r.Operator==FilterOperator.Between?" to "+r.UpperValue:"")))+(structuredFilter.Rules.Count>3?"; …":""));
        await RefreshPreview(rebuild: false, resetPage: true); Status.Text = Filtering.Summary(structuredFilter);
    }
}
