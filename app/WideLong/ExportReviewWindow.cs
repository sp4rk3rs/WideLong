using System.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WideLong;

public partial class MainWindow
{
    private bool ReviewExports(IReadOnlyDictionary<string,View> exports, Layout? chosen, ExportTarget target, bool zip, string name, IReadOnlyDictionary<string,string> cleaning, string? codeProblem)
    {
        var prepared = exports.Select(pair => (pair.Key, Info:Export.Review(pair.Value,target,zip,Export.ShapeName(name,pair.Key),cleaning.ContainsKey(pair.Key)))).ToList();
        var dialog = new FormDialog(this,"Review your export","Check the actual output before choosing its destination. This review stays in memory; it does not write any files.") { Width = 1010 };
        var tabs = new TabControl { MaxHeight = 530, Margin = new(0,8,0,0) };
        foreach (var pair in prepared)
        {
            var info = pair.Info;
            string shape = pair.Key.Length > 0 ? char.ToUpperInvariant(pair.Key[0]) + pair.Key[1..] : ShapeName(chosen!.Value);
            var body = new StackPanel { Margin = new(12) };
            body.Children.Add(new TextBlock { Text=$"{info.Rows:N0} rows × {info.Columns:N0} columns · {info.MissingValues:N0} missing cells",FontSize=17,FontWeight=FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text=info.MissingConvention,Margin=new(0,8,0,5),Foreground=(Brush)FindResource("Accent") });
            foreach(var notice in info.Notices) body.Children.Add(new TextBlock { Text=notice,Margin=new(0,3,0,3),FontSize=12 });
            body.Children.Add(new TextBlock { Text="Files: " + string.Join(", ",info.Files.Select(f => pair.Key.Length > 0 ? pair.Key+"/"+f : f)),Margin=new(0,6,0,8),FontSize=12 });
            body.Children.Add(new TextBlock { Text=$"First {info.Sample.Rows.Count} exported rows · actual column names and missing-value codes",Margin=new(0,6,0,6),FontWeight=FontWeights.SemiBold });
            body.Children.Add(ReadOnlyTable(info.Sample,190));
            var nameText = new TextBlock { Text=info.RenamedColumns.Count == 0 ? "All column names stay as displayed." : string.Join("\n",info.RenamedColumns.Select(n=>n.DisplayName+" → "+n.ExportName)),FontSize=12,Margin=new(8),TextWrapping=TextWrapping.Wrap };
            body.Children.Add(new Expander { Header=info.RenamedColumns.Count == 0 ? "Column names: unchanged" : $"{info.RenamedColumns.Count} column names normalized for SAS compatibility",Content=new ScrollViewer { Content=nameText,MaxHeight=90,VerticalScrollBarVisibility=ScrollBarVisibility.Auto },Margin=new(0,9,0,0),IsExpanded=info.RenamedColumns.Count>0 });
            var summaryRows = info.Summaries.Select(v => new Cell[] { new(v.Name), new(v.Kind.ToString()), new(v.N.ToString("N0")), new($"{v.Missing:N0} ({v.MissingShare:P0})"), new(v.Distinct.ToString("N0")), new(v.Min ?? "—"), new(v.Max ?? "—"), new(v.Mean is { } mean ? mean.ToString("G6", System.Globalization.CultureInfo.CurrentCulture) : "—"), new(v.Flags.Length == 0 ? "" : "Check: " + v.Flags) }).ToList();
            var summaryView = new View { Fields = new[] { "Variable", "Type", "N", "Missing", "Distinct", "Minimum", "Maximum", "Mean", "Check" }.Select(h => new OutputField(h, ValueKind.Text, "")).ToList(), Rows = summaryRows };
            int flagged = info.Summaries.Count(v => v.Flags.Length > 0);
            body.Children.Add(new Expander { Header = $"Variable summary · {info.Summaries.Count:N0} variables" + (flagged > 0 ? $" · {flagged:N0} to check" : ""), Content = ReadOnlyTable(summaryView, Math.Min(260, 40 + 26 * Math.Max(1, summaryRows.Count))), Margin = new(0, 9, 0, 0), IsExpanded = flagged > 0 });
            var tab = new TabItem { Header=shape+$" · {info.Rows:N0} × {info.Columns:N0}",Content=new ScrollViewer { Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto } };
            tabs.Items.Add(tab);
            if (shape == ShapeName(layout)) tabs.SelectedItem=tab;
        }
        if (tabs.SelectedIndex < 0) tabs.SelectedIndex=0;
        dialog.Add(tabs);
        if (codeProblem != null) dialog.Add(new TextBlock { Text = "Cleaning code not included: " + codeProblem, Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0), FontSize = 12 });
        dialog.Description(StructuredFilterSummary+(RowFilter.Text.Length>0?" · Search rows: "+(RowFilter.Text.Length>100?RowFilter.Text[..100]+"…":RowFilter.Text):"")+". Sorting and names use each layout's preferences. Review all tabs for a Both export. Choose a different shape or software with Cancel.");
        return dialog.Finish("Choose destination…") == true;
    }

    internal static DataGrid ReadOnlyTable(View data, double height=240)
    {
        var table=new DataTable();
        for(int c=0;c<data.Fields.Count;c++) table.Columns.Add("c"+c,typeof(string));
        foreach(var row in data.Rows) table.Rows.Add(row.Select(c=>c.Missing?(object)"—":c.Value).ToArray());
        var grid=new DataGrid { AutoGenerateColumns=false,IsReadOnly=true,ItemsSource=table.DefaultView,Height=height,CanUserReorderColumns=false,CanUserSortColumns=false,ColumnHeaderHeight=35,MinColumnWidth=110 };
        for(int c=0;c<data.Fields.Count;c++) grid.Columns.Add(new DataGridTextColumn { Header=new TextBlock {Text=data.Fields[c].Name,TextWrapping=TextWrapping.NoWrap},Binding=new System.Windows.Data.Binding("c"+c),Width=160 });
        return grid;
    }
}
