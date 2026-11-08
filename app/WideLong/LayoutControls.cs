using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WideLong;

public partial class MainWindow
{
    private void UpdateLayoutHelp()
    {
        string group = SchemaNaming.GroupName(session);
        if(SchemaNaming.HasEntity(session)) group=SchemaNaming.EntityName(session)+" + "+group;
        TimeMode.Content = "ID + " + group;
        ViewingBadge.Text = "Viewing: " + ShapeName(layout);
        RowVariablesButton.Visibility = layout == Layout.Measurement ? Visibility.Visible : Visibility.Collapsed;
        LayoutExplanation.Text = layout switch
        {
            Layout.Person => "Wide · One row per ID. Roles/entities and repeated observations get separate columns.",
            Layout.Measurement => "Choose variables to place in variable/value rows. Other variables keep their own columns.",
            Layout.Source => "Rows from one source file. Edits change the working copy; originals stay untouched.",
            _ => "Long · One row per ID + " + group + ". Each variable has its own column."
        };
    }
    private static string ShapeName(Layout shape) => shape switch { Layout.Person => "Wide", Layout.PersonTime => "Long", Layout.Measurement => "Custom", _ => "Source rows" };
    private async void RowVariables_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session.Sources.Count == 0 || !await CommitPendingEdit()) return;
        try
        {
            bool stack = CombineMode.SelectedIndex == 1;
            var fields = Engine.Build(session, Layout.PersonTime, (MatchMode)MatchRule.SelectedIndex, stack).Fields.Where(f=>!f.Key.StartsWith('@')).ToList();
            var selected = Preferences.RowVariables ?? new HashSet<string>();
            var d = new FormDialog(this, "Choose custom variables", "This optional layout puts selected variable names in one column and their values in another. Start with one family below, or choose your own variables. Unchecked variables stay as context columns.") { Width = 690 };
            var checks = new List<(string Key, CheckBox Check)>();
            var families = session.Sources.SelectMany(s => s.Fields.Where(f => f.Keep && f.Measure.Length > 0).GroupBy(f => f.Measure).Select(g => (Source: s, Name: g.Key, Fields: g.ToList()))).ToList();
            if (families.Count > 0)
            {
                var familyChoice = d.Choice("Start from a repeated-column family (optional)", new[] { "Choose a family…" }.Concat(families.Select(f => f.Source.Name + " / " + f.Name)).ToArray(), 0);
                var example = new TextBlock { Text = "Choose one family to see its columns and a before/after example. You can also select variables yourself below.", FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new(0, 6, 0, 10) }; d.Add(example);
                familyChoice.SelectionChanged += (_, _) =>
                {
                    if (familyChoice.SelectedIndex < 1) return;
                    var family = families[familyChoice.SelectedIndex - 1];
                    string key = stack ? "stack:" + family.Name : family.Source.Key + ":measure:" + family.Name;
                    foreach (var c in checks) c.Check.IsChecked = c.Key == key;
                    string Before(Field field) { int c = family.Source.Fields.IndexOf(field); return field.Name + " = " + (family.Source.Rows.FirstOrDefault()?[c] ?? "(empty)"); }
                    var first = family.Fields[0]; int column = family.Source.Fields.IndexOf(first);
                    example.Text = "Before: " + string.Join(", ", family.Fields.Take(3).Select(Before)) + "\nAfter: " + SchemaNaming.GroupName(session) + " = " + first.Occasion + ", variable = " + family.Name + ", value = " + (family.Source.Rows.FirstOrDefault()?[column] ?? "(empty)") + ". One row per grouping value; the ID and context columns stay beside it.";
                };
            }
            else d.Description("No repeated column families detected. Choose variables below to use custom stacking.");
            var list = new StackPanel();
            foreach (var field in fields)
            {
                var text = new StackPanel(); text.Children.Add(new TextBlock { Text = field.Name, FontWeight = FontWeights.SemiBold });
                text.Children.Add(new TextBlock { Text = field.Source + " · " + field.Kind, Foreground = (Brush)FindResource("Muted"), FontSize = 11, Margin = new(0, 2, 0, 0) });
                var check = new CheckBox { Content = text, IsChecked = selected.Contains(field.Key), Margin = new(0, 6, 0, 8) }; checks.Add((field.Key, check)); list.Children.Add(check);
            }
            d.Add(new ScrollViewer { Content = list, MaxHeight = 270, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            d.Description("Different types share a Text value column. Numbered items such as question_1 and question_2 are not assumed to be time points.");
            if (d.Finish("Use these variables") != true) return;
            var chosen = checks.Where(c => c.Check.IsChecked == true).Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            if (chosen.Count == 0) { MessageBox.Show(this, "Choose at least one variable to turn into rows."); return; }
            Preferences.RowVariables = chosen; await RefreshPreview(); Status.Text = $"{chosen.Count} variables become rows · other variables remain in their own columns";
        }
        catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message, "Custom variables", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private static (CheckBox Blank, TextBox Tokens) MissingControls(FormDialog d, HashSet<string> tokens, bool collapsed=false)
    {
        var panel=new StackPanel();
        var blank = new CheckBox { Content = "Treat empty cells as missing", IsChecked = tokens.Contains(""), Margin = new(0, 12, 0, 4) }; panel.Children.Add(blank);
        panel.Children.Add(new TextBlock {Text="Other missing-value codes (one per line, exact match)",FontWeight=FontWeights.SemiBold,Margin=new(0,12,0,4)});
        var extra = new TextBox {Text=string.Join("\n", tokens.Where(t => t.Length > 0))};panel.Children.Add(extra);
        extra.AcceptsReturn = true; extra.MinHeight = 54; extra.MaxHeight = 100; extra.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        panel.Children.Add(new TextBlock{Text="NA, NULL and . remain literal text unless listed here. Numeric, date and Boolean blanks are always missing.",FontSize=12,Margin=new(0,6,0,10)});
        if(collapsed)d.Add(new Expander{Header="File default missing-value rules",Content=panel,IsExpanded=false,Margin=new(0,8,0,8)});else d.Add(panel);
        return (blank, extra);
    }
    private static HashSet<string> ReadMissing((CheckBox Blank, TextBox Tokens) controls)
    {
        var tokens = controls.Tokens.Text.Replace("\r", "").Split('\n').Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (controls.Blank.IsChecked == true) tokens.Add(""); return tokens;
    }
}
