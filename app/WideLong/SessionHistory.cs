using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace WideLong;

public sealed class ActivityEntry
{
    public string Time { get; init; } = DateTime.Now.ToString("HH:mm:ss");
    public string Category { get; init; } = "";
    public string Description { get; init; } = "";
    public string State { get; set; } = "Applied";
    public List<HistoryDetail> Details { get; init; } = [];
    public string Summary { get; init; } = "";
}
public sealed record HistoryDetail(string File, string Record, string Variable, string Before, string After);

public partial class MainWindow
{
    private readonly List<ActivityEntry> activities = [];
    private readonly Stack<ActivityEntry> undoActivities = new(), redoActivities = new();
    private void RecordActivity(string category,string description)
    {
        bool isView=category is "View" or "Filters" or "Navigation";
        activities.Add(new() {Category=category,Description=description,State=isView?"View preference":"Completed",Summary=isView?"This is a view preference. Undo/Redo changes data; use the view controls to change this preference.":"No dataset or history file is saved automatically."});
        UpdateHistoryCount();
    }
    private void RecordDataChange(Session before,string description)
    {
        var details=new List<HistoryDetail>();long changed=0;
        void Detail(string file,string record,string variable,string oldValue,string newValue) { if(details.Count<50) details.Add(new(file,record,variable,oldValue,newValue)); }
        foreach(var source in session.Sources)
        {
            var old=before.Sources.FirstOrDefault(s=>s.Key==source.Key);
            if(old==null) { Detail(source.Name,"—","Source file","Not in session",$"{source.Rows.Count:N0} rows / {source.Fields.Count:N0} columns");continue; }
            if(old.IdColumn!=source.IdColumn) Detail(source.Name,"—","ID column",old.IdColumn<0?"None":old.Fields[old.IdColumn].Name,source.IdColumn<0?"None":source.Fields[source.IdColumn].Name);
            if(old.TimeColumn!=source.TimeColumn) Detail(source.Name,"—","Grouping column",old.TimeColumn<0?"None":old.Fields[old.TimeColumn].Name,source.TimeColumn<0?"None":source.Fields[source.TimeColumn].Name);
            if(old.EntityColumn!=source.EntityColumn) Detail(source.Name,"—","Additional matching column",old.EntityColumn<0?"None":old.Fields[old.EntityColumn].Name,source.EntityColumn<0?"None":source.Fields[source.EntityColumn].Name);
            for(int c=0;c<Math.Min(old.Fields.Count,source.Fields.Count);c++)
            {
                var a=old.Fields[c];var b=source.Fields[c];
                if(a.Name!=b.Name) Detail(source.Name,"—",a.Name,a.Name,b.Name);
                if(a.Kind!=b.Kind) Detail(source.Name,"—",b.Name+" type",a.Kind.ToString(),b.Kind.ToString());
                if(a.Keep!=b.Keep) Detail(source.Name,"—",b.Name,a.Keep?"Kept":"Excluded",b.Keep?"Kept":"Excluded");
                if(a.Measure!=b.Measure||a.Occasion!=b.Occasion) Detail(source.Name,"—",b.Name+" mapping",a.Measure+" / "+a.Occasion,b.Measure+" / "+b.Occasion);
                string Missing(Source s,int column)=>string.Join(" | ",s.EffectiveMissingTokens(column).OrderBy(t=>t,StringComparer.Ordinal).Select(t=>t.Length==0?"(empty)":t));
                if(Missing(old,c)!=Missing(source,c)) Detail(source.Name,"—",b.Name+" missing rules",Missing(old,c),Missing(source,c));
            }
            foreach(int c in source.Steps.Skip(old.Steps.Count).Select(s=>s.Column).Distinct())
                for(int r=0;r<Math.Min(old.Rows.Count,source.Rows.Count);r++) if(old.Rows[r][c]!=source.Rows[r][c])
                { changed++;Detail(source.Name,(r+1).ToString(),source.Fields[c].Name,old.Rows[r][c],source.Rows[r][c]); }
        }
        foreach(var old in before.Sources.Where(s=>session.Sources.All(n=>n.Key!=s.Key))) Detail(old.Name,"—","Source file","In session","Removed from session");
        var entry=new ActivityEntry { Category="Data",Description=description,Details=details,Summary=$"{changed:N0} stored cell values changed. Details show up to 50 affected values or settings. Missing-rule changes reinterpret values without rewriting the original text. Undo/Redo covers the most recent 12 data changes." };
        activities.Add(entry);undoActivities.Push(entry);redoActivities.Clear();
        if(undoActivities.Count>12){var kept=undoActivities.Take(12).Reverse().ToArray();undoActivities.Clear();foreach(var item in kept)undoActivities.Push(item);}
        UpdateHistoryCount();
    }
    private void UpdateHistoryCount() { if(HistoryButton!=null) HistoryButton.Content="History ("+activities.Count+")"; }
    private void History_Click(object sender,RoutedEventArgs e)
    {
        var dialog=new FormDialog(this,"Session history","This history lives only in memory. Undo/Redo covers data changes; view preferences are listed separately and changed through their controls."){Width=1010};
        if(activities.Count==0){dialog.Description("No changes yet. Imports, edits, mapping changes and view activities will appear here.");dialog.Finish("Close",false);return;}
        var rows=activities.AsEnumerable().Reverse().ToList();
        var list=new DataGrid { ItemsSource=rows,AutoGenerateColumns=false,IsReadOnly=true,Height=Math.Min(190,Math.Max(80,rows.Count*33+35)),CanUserReorderColumns=false,SelectionMode=DataGridSelectionMode.Single,SelectionUnit=DataGridSelectionUnit.FullRow,ColumnHeaderHeight=32 };
        foreach(var (label,property,width) in new[]{("Time","Time",80.0),("Category","Category",80.0),("Action","Description",610.0),("State","State",140.0)}) list.Columns.Add(new DataGridTextColumn {Header=label,Binding=new System.Windows.Data.Binding(property),Width=width});
        dialog.Add(list);
        var summary=new TextBlock { Margin=new(0,12,0,8) };dialog.Add(summary);
        var holder=new ContentControl();dialog.Add(holder);
        list.SelectionChanged+=(_,_)=>
        {
            if(list.SelectedItem is not ActivityEntry activity)return;summary.Text=activity.Summary;
            holder.Content=activity.Details.Count==0?new TextBlock {Text="No source values changed.",Margin=new(0,8,0,8)}:ReadOnlyTable(new View {Fields=new[]{"File","Data record","Variable / setting","Before","After"}.Select(n=>new OutputField(n,ValueKind.Text,"history")).ToList(),Rows=activity.Details.Select(d=>new[]{d.File,d.Record,d.Variable,d.Before,d.After}.Select(v=>new Cell(v,MissingFlag:false)).ToArray()).ToList()},Math.Min(180,Math.Max(75,activity.Details.Count*33+35)));
        };
        list.SelectedIndex=0;dialog.Finish("Close",false);
    }
    private void ClearHistory(){activities.Clear();undoActivities.Clear();redoActivities.Clear();UpdateHistoryCount();}
}
