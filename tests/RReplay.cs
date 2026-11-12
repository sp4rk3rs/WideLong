using WideLong;
using System.Diagnostics;
using System.Text;

/// <summary>Writes the original source files and generated R code, runs native R, and requires the replayed data to equal the application preview.</summary>
static class RReplay
{
    public static void Run(string root, string rscript, string name, Session s, Layout layout, bool stack=false, MatchMode match=MatchMode.AllPeople, string filter="", string[]? rowNames=null, RowFilterSpec? rules=null, bool allowUnsupportedSas=false, ViewPreferences? prefs=null, int timeoutMs=90000)
    {
        string folder=Path.Combine(root,"recipe-cases",name); Directory.CreateDirectory(folder);
        bool customView=prefs!=null; prefs??=new ViewPreferences();
        if(layout==Layout.Measurement && rowNames==null) {
            prefs.RowVariables=Engine.SuggestedRowVariables(s,stack);
            if(prefs.RowVariables.Count==0) prefs.RowVariables=Engine.Build(s,Layout.PersonTime,match,stack).Fields.Skip(2).Where(f=>f.Kind==ValueKind.Number).Select(f=>f.Key).ToHashSet(StringComparer.Ordinal);
        }
        if(rowNames!=null) prefs.RowVariables=Engine.Build(s,Layout.PersonTime,match,stack).Fields.Where(f=>rowNames.Contains(f.Name,StringComparer.Ordinal)).Select(f=>f.Key).ToHashSet(StringComparer.Ordinal);
        var raw=layout==Layout.Source?Engine.SourceView(s.Sources[0]):Engine.Build(s,layout,match,stack,prefs.RowVariables);
        prefs.Reconcile(raw.Fields.Select(f=>f.Key));
        if(!customView && raw.Fields.Count>2)
        {
            prefs.ToggleSort(raw.Fields[2].Key); prefs.Sorts[0]=prefs.Sorts[0] with {Descending=true}; prefs.ToggleSort(raw.Fields[0].Key);
            prefs.ReorderColumns(raw.Fields.Select(f=>f.Key).Reverse().ToArray(),raw.Fields[^1].Key);
            prefs.Names[raw.Fields[0].Key]="chosen_identifier";
        }
        var expected=Presentation.Apply(Filtering.Apply(raw,rules),prefs.Copy(),filter);
        foreach(var source in s.Sources)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding=source.EncodingName=="windows-1252"?Encoding.GetEncoding(1252):source.EncodingName=="UTF-16LE"?Encoding.Unicode:new UTF8Encoding(false);
            using var writer=new StreamWriter(Path.Combine(folder,source.Name),false,encoding);
            string Escape(string value)=>value.Length==0 || value.Contains(source.Delimiter) || value.IndexOfAny(['"','\r','\n'])>=0?"\""+value.Replace("\"","\"\"")+"\"":value;
            writer.WriteLine(string.Join(source.Delimiter,source.Fields.Take(source.FileColumns).Select(f=>Escape(f.Original))));
            foreach(var row in source.SkippedRows)writer.WriteLine(string.Join(source.Delimiter,row.Select(Escape)));
            foreach(var row in source.OriginalRows!)writer.WriteLine(string.Join(source.Delimiter,row.Select(Escape)));
        }
        var plan=Recipe.Plan(s,layout,match,stack,s.Sources[0].Key,prefs,filter,rules);
        File.WriteAllText(Path.Combine(folder,"clean.R"),Recipe.R(plan,true));
        try { File.WriteAllText(Path.Combine(folder,"clean.sas"),SasRecipe.Generate(plan,true)); }
        catch(InvalidOperationException) when(allowUnsupportedSas||s.Sources.SelectMany(source=>source.Rows).SelectMany(row=>row).Any(v=>v.Contains('\n')||v!=v.Trim())) { /* Exact text cases are intentionally supported by R only. */ }
        var expectedFile=new View { Fields=expected.Fields, Rows=expected.Rows.Select(row=>row.Select(c=>new Cell(c.Missing?"__DH_EXPECTED_NA__":c.Value,MissingFlag:false)).ToArray()).ToList() };
        Export.SaveCsv(Path.Combine(folder,"expected.csv"),expectedFile);
        if(File.Exists(Path.Combine(folder,"clean.sas"))) Export.SaveBundle(Path.Combine(folder,"expected-bundle.zip"),expected,ExportTarget.Sas);
        var kinds="c("+string.Join(",",expected.Fields.Select(f=>Recipe.RQuote(f.Kind.ToString())))+")";
        string verify="source('clean.R', encoding = 'UTF-8')\n"+
            "expected_text <- iconv(list(readBin('expected.csv', 'raw', file.size('expected.csv'))), from = 'UTF-8', to = 'UTF-8')\n"+
            "expected <- read.csv(text = sub('\\r?\\n$', '', expected_text), blank.lines.skip = FALSE, colClasses = 'character', check.names = FALSE, na.strings = '__DH_EXPECTED_NA__', encoding = 'UTF-8', comment.char = '')\n"+
            "kinds <- "+kinds+"\n"+
            "for (i in seq_along(kinds)) { z <- expected[[i]]; expected[[i]] <- switch(kinds[[i]], Number = as.numeric(z), Date = as.Date(z), Boolean = ifelse(is.na(z), NA, z == 'true'), z) }\n"+
            "if (!identical(names(prepared), names(expected))) stop('Names differ: ', paste(names(prepared), collapse = ','), ' vs ', paste(names(expected), collapse = ','))\n"+
            "if (nrow(prepared) != nrow(expected)) stop('Row counts differ: ', nrow(prepared), ' vs ', nrow(expected))\n"+
            "for (i in seq_along(expected)) {\n"+
            "  same <- identical(class(prepared[[i]]), class(expected[[i]])) && isTRUE(all.equal(prepared[[i]], expected[[i]], check.attributes = FALSE, tolerance = 1e-12))\n"+
            "  if (!same) { bad <- which(!(is.na(prepared[[i]]) & is.na(expected[[i]])) & (is.na(prepared[[i]]) != is.na(expected[[i]]) | as.character(prepared[[i]]) != as.character(expected[[i]])))[1]; stop('Column mismatch ', names(expected)[i], ' (', class(prepared[[i]])[1], ' vs ', class(expected[[i]])[1], ') row ', bad, ': ', prepared[[i]][bad], ' vs ', expected[[i]][bad]) }\n"+
            "}\n"+
            "cat('PASS native R replay matches application preview\\n')\n";
        File.WriteAllText(Path.Combine(folder,"verify.R"),verify);
        var start=new ProcessStartInfo(rscript) {WorkingDirectory=folder,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true}; start.ArgumentList.Add("--vanilla"); start.ArgumentList.Add("verify.R"); start.Environment["LC_ALL"]="C";
        using var process=Process.Start(start)!; var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(timeoutMs)) {process.Kill(true); throw new Exception("R replay timed out: "+name);}
        Task.WaitAll(stdout,stderr); if(process.ExitCode!=0) { File.WriteAllText(Path.Combine(folder,"failure.txt"),stdout.Result+"\n"+stderr.Result); throw new Exception(name+": "+stderr.Result.Replace("\r","").Replace("\n"," | ")); }
        Console.WriteLine("  Native R verified: "+name+" ("+expected.Rows.Count+" rows)");
    }
}
