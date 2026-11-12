using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WideLong;

public partial class MainWindow
{
    private async void Code_Click(object sender, RoutedEventArgs e)
    {
        if (busy || hasError || !await CommitPendingEdit()) return;
        filterTimer.Stop(); await RefreshPreview(rebuild: false); if (busy || hasError) return;
        try
        {
            var plan = Recipe.Plan(session, layout, (MatchMode)MatchRule.SelectedIndex, CombineMode.SelectedIndex == 1, selectedSource?.Key, Preferences, RowFilter.Text, structuredFilter);
            new CodeWindow(this, plan).ShowDialog();
        }
        catch (InvalidOperationException ex) { MessageBox.Show(this, ex.Message, "Cleaning code", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}

internal sealed class CodeWindow : Window
{
    private readonly CodePlan plan;
    private readonly ComboBox language = new() { ItemsSource = new[] { "R", "SAS" }, SelectedIndex = 0, Width = 140, Margin = new(0, 0, 14, 0) };
    private readonly TextBox code = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12, IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(14), Background = new SolidColorBrush(Color.FromRgb(251, 249, 253)) };
    private readonly TextBlock status = new() { Margin = new(0, 8, 0, 8), FontSize = 12 };
    private readonly Button save = new() { Content = "Save code…", Style = (Style)Application.Current.FindResource("Primary") };
    private readonly Button copy = new() { Content = "Copy code" };
    private string? currentCode;
    /// <summary>R is listed first: its generated code is verified by running it in R; generated SAS has not yet been run in SAS.</summary>
    private bool Sas => language.SelectedIndex == 1;

    public CodeWindow(Window owner, CodePlan plan)
    {
        this.plan = plan; Owner = owner; Title = "Reproducible cleaning code"; Width = Math.Min(1100,SystemParameters.WorkArea.Width-20); Height = Math.Min(780,SystemParameters.WorkArea.Height-20); MinWidth = Math.Min(780,Width); MinHeight = Math.Min(560,Height); WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new(22) }; Content = root;
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "Keep the raw files. Rerun the cleaning.", FontSize = 24, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "A commented script that reads your original files and repeats every change, grouped and labelled, to rebuild this layout. Export data… can include it next to the dataset.", Margin = new(0, 8, 0, 12), Foreground = (Brush)FindResource("Muted") });
        var toolbar = new WrapPanel(); toolbar.Children.Add(language); toolbar.Children.Add(save); toolbar.Children.Add(copy);
        var examples = new Button { Content = "Save example files + code ZIP…", IsEnabled = plan.Session.Sources.All(s => s.ImportPath == null), ToolTip = "Built-in fictional examples only. Imported originals are not copied." }; toolbar.Children.Add(examples); heading.Children.Add(toolbar);
        heading.Children.Add(new TextBlock { Text = "Code can contain local paths and the values you explicitly corrected or recoded. It stays in memory until you choose Save or Copy.", FontSize = 11, Foreground = (Brush)FindResource("Muted"), Margin = new(0, 10, 0, 2) });
        heading.Children.Add(status); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading); root.Children.Add(code);
        language.SelectionChanged += (_, _) => Generate(); save.Click += (_, _) => Save(); copy.Click += (_, _) => { if (currentCode != null) { Clipboard.SetText(currentCode); status.Text = "Code copied to your clipboard."; } };
        examples.Click += (_, _) => SaveExamples(); Generate();
    }

    private void Generate()
    {
        try
        {
            currentCode = Sas ? SasRecipe.Generate(plan) : Recipe.R(plan); code.Text = currentCode;
            status.Foreground = (Brush)FindResource("Accent"); status.Text = Sas ? "SAS: reads each original file, cleans it, combines the files and ends with WORK.PREPARED_DATA. Edit the folder line for your SAS machine. Generated SAS has not been run in SAS here." : "R: reads each original file, cleans it, combines the files and ends with `prepared`. Base R only; no packages or network.";
            save.IsEnabled = copy.IsEnabled = true;
        }
        catch (InvalidOperationException ex) { currentCode = null; code.Clear(); status.Foreground = Brushes.DarkRed; status.Text = ex.Message; save.IsEnabled = copy.IsEnabled = false; }
    }
    private bool Original(string path) => plan.Session.Sources.Any(s => s.ImportPath != null && string.Equals(Path.GetFullPath(path), s.ImportPath, StringComparison.OrdinalIgnoreCase));
    private void Save()
    {
        if (currentCode == null) return;
        var picker = new SaveFileDialog { Title = "Save reproducible cleaning code", FileName = "clean", Filter = Sas ? "SAS program|*.sas" : "R script|*.R", AddExtension = true, OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        if (Original(picker.FileName)) { MessageBox.Show(this, "Choose a different filename. Original imports cannot be overwritten."); return; }
        try { File.WriteAllText(picker.FileName, currentCode, new UTF8Encoding(false)); status.Text = "Saved " + Path.GetFileName(picker.FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, ex.Message); }
    }
    private void SaveExamples()
    {
        var picker = new SaveFileDialog { Title = "Save fictional raw files and reproducible code", FileName = "example_with_cleaning_code.zip", Filter = "Example and code ZIP|*.zip", OverwritePrompt = true };
        if (picker.ShowDialog(this) != true) return;
        try { Recipe.SaveExampleBundle(picker.FileName, plan); status.Text = "Saved fictional original files and cleaning scripts. Extract together before running."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { MessageBox.Show(this, ex.Message); }
    }
}
