using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace WideLong;

public partial class MainWindow
{
    private async void MappingEditor_Click(object sender, RoutedEventArgs e)
    {
        if (busy || session.Sources.Count == 0 || !await CommitPendingEdit()) return;
        OpenMappingEditor();
    }
    private void OpenMappingEditor(Source? source = null)
    {
        if (busy || session.Sources.Count == 0) return;
        CloseHover();
        if (source == null)
        {
            var chooser = new FormDialog(this, "Map repeated columns", "Choose the file whose columns repeat the same variables for different grouping values.");
            int preferred = session.Sources.FindIndex(s => s.Key == selectedSource?.Key && s.Fields.Any(f=>f.Measure.Length>0));
            if(preferred<0) preferred=session.Sources.Select((s,i)=>(Count:s.Fields.Count(f=>f.Measure.Length>0),Index:i)).OrderByDescending(x=>x.Count).First().Index;
            var file = chooser.Choice("Source file", session.Sources.Select(s => s.Name).ToArray(), preferred);
            if (chooser.Finish("Open mapping table") != true) return;
            source = session.Sources[file.SelectedIndex];
        }
        if (source.Fields.Count == 0) return;
        var mappings = new ObservableCollection<ColumnMapping>(RepeatedColumnMapping.FromSource(source));
        if (mappings.Count == 0) { MessageBox.Show(this, "This file contains only ID/grouping columns. There are no other columns to map.", "Map repeated columns"); return; }
        while (true)
        {
            var dialog = new FormDialog(this, "Map repeated columns · " + source.Name, "Each mapped column becomes one shared variable at one grouping value. Edit cells individually, or select several rows and use the batch controls. ID/grouping key columns remain unchanged.") { Width = 1080, MaxHeight = Math.Max(600, Math.Min(900, SystemParameters.WorkArea.Height - 40)) };
            var table = new DataGrid { ItemsSource = mappings, AutoGenerateColumns = false, SelectionUnit = DataGridSelectionUnit.FullRow, SelectionMode = DataGridSelectionMode.Extended, Height = 220, RowHeight = 30, ColumnHeaderHeight = 32, CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = false, Margin = new(0, 5, 0, 8) };
            table.Columns.Add(new DataGridCheckBoxColumn { Header = "Keep", Binding = new Binding(nameof(ColumnMapping.Keep)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 55 });
            table.Columns.Add(new DataGridTextColumn { Header = "Source column", Binding = new Binding(nameof(ColumnMapping.Name)), IsReadOnly = true, Width = 230 });
            table.Columns.Add(new DataGridTextColumn { Header = "Type", Binding = new Binding(nameof(ColumnMapping.Kind)), IsReadOnly = true, Width = 85 });
            table.Columns.Add(new DataGridTextColumn { Header = "Shared variable", Binding = new Binding(nameof(ColumnMapping.Variable)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 260 });
            table.Columns.Add(new DataGridTextColumn { Header = "Grouping value", Binding = new Binding(nameof(ColumnMapping.Grouping)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 260 });
            dialog.Add(table);
            dialog.Description("Select adjacent rows with Shift, or individual rows with Ctrl. Leave both mapping cells empty for an ordinary column. Keep controls whether that source column appears in output.");
            var batch = new Grid(); batch.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); batch.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); batch.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var variablePanel = new StackPanel { Margin = new(0, 0, 12, 0) }; variablePanel.Children.Add(new TextBlock { Text = "Shared variable for selected rows", FontWeight = FontWeights.SemiBold, FontSize = 12 }); var variable = new TextBox { Margin = new(0, 5, 0, 0) }; variablePanel.Children.Add(variable);
            var groupingPanel = new StackPanel { Margin = new(0, 0, 12, 0) }; groupingPanel.Children.Add(new TextBlock { Text = "Grouping labels", FontWeight = FontWeights.SemiBold, FontSize = 12 }); var strategy = new ComboBox { ItemsSource = new[] { "Keep labels; infer empty labels from suffix", "Infer every label from the column suffix", "Use the manual label below" }, SelectedIndex = 0 }; groupingPanel.Children.Add(strategy);
            var manualPanel = new StackPanel(); manualPanel.Children.Add(new TextBlock { Text = "Manual grouping label", FontWeight = FontWeights.SemiBold, FontSize = 12 }); var manual = new TextBox { Margin = new(0, 5, 0, 0) }; manualPanel.Children.Add(manual);
            batch.Children.Add(variablePanel); Grid.SetColumn(groupingPanel, 1); batch.Children.Add(groupingPanel); Grid.SetColumn(manualPanel, 2); batch.Children.Add(manualPanel); dialog.Add(batch);
            var actions = new WrapPanel { Margin = new(0, 9, 0, 5) }; var assign = SmallButton("Assign to selected rows"); var clear = SmallButton("Clear selected mappings"); var update = SmallButton("Update preview"); actions.Children.Add(assign); actions.Children.Add(clear); actions.Children.Add(update); dialog.Add(actions);
            var validation = new TextBlock { FontSize = 12, Margin = new(0, 4, 0, 7) }; dialog.Add(validation);
            var before = new TextBlock { Foreground = (Brush)FindResource("Muted"), FontSize = 12, Margin = new(0, 0, 0, 7) }; dialog.Add(before);
            var preview = ReviewGrid(155);
            ReviewColumn(preview, "ID", nameof(MappingPreviewRow.Id), 120);
            ReviewColumn(preview, "Grouping", nameof(MappingPreviewRow.Grouping), 140);
            ReviewColumn(preview, "Shared variable", nameof(MappingPreviewRow.Variable), 230);
            ReviewColumn(preview, "Value", nameof(MappingPreviewRow.Value), 160);
            ReviewColumn(preview, "Original source column", nameof(MappingPreviewRow.SourceColumn), 280);
            dialog.Add(preview);
            dialog.Description("The preview uses the first source record and up to 20 kept mapped columns. Grouping labels match other files exactly: m3 and Month 3 are different. Suffix inference is an explicit batch action; it never changes source values.");
            void CommitGrid() { table.CommitEdit(DataGridEditingUnit.Cell, true); table.CommitEdit(DataGridEditingUnit.Row, true); }
            void UpdatePreview()
            {
                var errors = RepeatedColumnMapping.Validate(source, mappings);
                validation.Text = errors.Count == 0 ? $"Ready: {mappings.Count(m => m.Keep && m.Variable.Length > 0):N0} kept repeated columns · {mappings.Where(m => m.Keep && m.Variable.Length > 0).Select(m => m.Variable.Trim()).Distinct(StringComparer.Ordinal).Count():N0} shared variables" : string.Join("\n", errors.Take(3)) + (errors.Count > 3 ? $"\n{errors.Count - 3:N0} more problems to resolve." : "");
                validation.Foreground = errors.Count == 0 ? (Brush)FindResource("Accent") : Brushes.DarkRed;
                var mapped = mappings.Where(m => m.Keep && !string.IsNullOrWhiteSpace(m.Variable) && !string.IsNullOrWhiteSpace(m.Grouping)).Take(20).ToList();
                string id = source.Rows.Count > 0 && source.IdColumn >= 0 ? source.Rows[0][source.IdColumn] : "(ID not selected)";
                string Cell(ColumnMapping mapping) => source.Rows.Count == 0 ? "(no source rows)" : source.IsMissing(source.Rows[0][mapping.Column], mapping.Column) ? "—" : source.Rows[0][mapping.Column];
                before.Text = mapped.Count == 0 ? "Before / after: map a column to see how its original value becomes a grouped row." : "Before (first source record): " + string.Join(" · ", mapped.Take(4).Select(m => m.Name + " = " + Cell(m))) + (mapped.Count > 4 ? " · …" : "");
                preview.ItemsSource = mapped.Select(m => new MappingPreviewRow(id, m.Grouping, m.Variable.Trim(), Cell(m), m.Name)).ToList();
            }
            assign.Click += (_, _) =>
            {
                CommitGrid(); var selected = table.SelectedItems.Cast<ColumnMapping>().ToArray();
                if (selected.Length == 0) { MessageBox.Show(dialog, "Select the source columns to map first."); return; }
                if (string.IsNullOrWhiteSpace(variable.Text)) { MessageBox.Show(dialog, "Enter a shared variable name for these columns."); return; }
                if (strategy.SelectedIndex == 2 && string.IsNullOrWhiteSpace(manual.Text)) { MessageBox.Show(dialog, "Enter a grouping label, or choose a suffix option."); return; }
                foreach (var mapping in selected)
                {
                    mapping.Variable = variable.Text.Trim();
                    if (strategy.SelectedIndex == 2) mapping.Grouping = manual.Text;
                    else if (strategy.SelectedIndex == 1 || string.IsNullOrWhiteSpace(mapping.Grouping)) mapping.Grouping = RepeatedColumnMapping.InferGrouping(mapping.Name);
                }
                UpdatePreview();
            };
            clear.Click += (_, _) => { CommitGrid(); foreach (var mapping in table.SelectedItems.Cast<ColumnMapping>()) { mapping.Variable = ""; mapping.Grouping = ""; } UpdatePreview(); };
            update.Click += (_, _) => { CommitGrid(); UpdatePreview(); };
            table.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(UpdatePreview, DispatcherPriority.Background);
            UpdatePreview();
            if (dialog.Finish("Apply mappings") != true) return;
            CommitGrid(); var failures = RepeatedColumnMapping.Validate(source, mappings);
            if (failures.Count > 0) { MessageBox.Show(this, string.Join("\n", failures.Take(6)), "Mappings need attention", MessageBoxButton.OK, MessageBoxImage.Information); continue; }
            if (mappings.All(m => source.Fields[m.Column].Keep == m.Keep && source.Fields[m.Column].Measure == m.Variable.Trim() && source.Fields[m.Column].Occasion == (string.IsNullOrWhiteSpace(m.Grouping) ? "" : m.Grouping))) return;
            var selectedSource = source;
            Change(() => RepeatedColumnMapping.Apply(selectedSource, mappings), $"Updated repeated-column mappings in {source.Name} · {mappings.Count(m => m.Variable.Trim().Length > 0):N0} mapped columns");
            return;
        }
    }
    private sealed record MappingPreviewRow(string Id, string Grouping, string Variable, string Value, string SourceColumn);
}
