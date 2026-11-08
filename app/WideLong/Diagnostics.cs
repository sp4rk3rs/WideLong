namespace WideLong;

public enum MatchIssueKind { MissingId, MissingEntity, MissingGrouping, DuplicateKey, UnmatchedId, UnconfiguredKey }
public sealed record MatchIssue(string SourceKey, int Row, int Column, MatchIssueKind Kind, string Id, string Grouping, string Detail, string OtherSourceKey = "", string Entity = "")
{
    public int Record => Row + 1;
    public string Problem => Kind switch { MatchIssueKind.MissingId => "Missing ID", MatchIssueKind.MissingEntity => "Missing additional key", MatchIssueKind.MissingGrouping => "Missing grouping", MatchIssueKind.DuplicateKey => "Duplicate key", MatchIssueKind.UnmatchedId => "Unmatched ID", _ => "Choose key columns" };
}
public sealed record SharedIdCount(string OtherSourceKey, string OtherFile, int Shared, int ThisFileIds, int OtherFileIds)
{
    public string Coverage => ThisFileIds == 0 ? "—" : $"{100d * Shared / ThisFileIds:0.#}%";
}
public sealed class FileMatchReport
{
    public required string SourceKey { get; init; }
    public required string File { get; init; }
    public string IdColumn { get; init; } = "Choose…";
    public string EntityColumn { get; init; } = "None";
    public string GroupingColumn { get; init; } = "None";
    public int Rows { get; init; }
    public int DistinctIds { get; init; }
    public int DistinctKeys { get; init; }
    public int MissingIdRows { get; init; }
    public int MissingEntityRows { get; init; }
    public int MissingGroupingRows { get; init; }
    public int DuplicateKeys { get; init; }
    public int DuplicateRows { get; init; }
    public int ExtraDuplicateRows { get; init; }
    public bool Configured { get; init; }
    public string IdReason { get; init; } = "";
    public string EntityReason { get; init; } = "";
    public string GroupingReason { get; init; } = "";
    public List<MatchIssue> Issues { get; } = [];
    public List<SharedIdCount> SharedIds { get; } = [];
    public HashSet<string> CompleteIds { get; init; } = new(StringComparer.Ordinal);
    public int MissingKeyRows => Issues.Where(i => i.Kind is MatchIssueKind.MissingId or MatchIssueKind.MissingEntity or MatchIssueKind.MissingGrouping).Select(i => i.Row).Distinct().Count();
    public string KeyStatus => !Configured ? "Choose keys" : MissingIdRows + MissingEntityRows + MissingGroupingRows + DuplicateRows == 0 ? "Complete, unique keys" : "Review problems";
}

public static class MatchingDiagnostics
{
    public static List<FileMatchReport> Analyze(Session session)
    {
        var reports = new List<FileMatchReport>();
        foreach (var source in session.Sources)
        {
            var configuredColumns = new[] { source.IdColumn, source.EntityColumn, source.TimeColumn }.Where(c => c >= 0).ToArray();
            bool configured = source.IdColumn >= 0 && source.EntityColumn >= -1 && source.TimeColumn >= -1 && configuredColumns.All(c => c < source.Fields.Count) && configuredColumns.Distinct().Count() == configuredColumns.Length;
            var issues = new List<MatchIssue>();
            var keys = new Dictionary<(string Id, string Entity, string Group), List<int>>();
            var completeIds = new HashSet<string>(StringComparer.Ordinal);
            int missingId = 0, missingEntity = 0, missingGroup = 0;
            if (!configured && source.Rows.Count > 0)
                issues.Add(new(source.Key, 0, Math.Min(source.Fields.Count - 1, Math.Max(0, source.IdColumn)), MatchIssueKind.UnconfiguredKey, "", "", "Choose an ID column and different optional additional-key/time columns. Several people sharing an ID and time need an additional key such as person or role."));
            if (configured)
            {
                for (int r = 0; r < source.Rows.Count; r++)
                {
                    if (!source.Keeps(r)) continue; // Left out by a response filter; never combined.
                    string id = source.Rows[r][source.IdColumn], entity = source.EntityColumn < 0 ? "" : source.Rows[r][source.EntityColumn], group = source.TimeColumn < 0 ? "" : source.Rows[r][source.TimeColumn];
                    bool badId = source.IsMissing(id, source.IdColumn) || string.IsNullOrWhiteSpace(id);
                    bool badEntity = source.EntityColumn >= 0 && (source.IsMissing(entity, source.EntityColumn) || string.IsNullOrWhiteSpace(entity));
                    bool badGroup = source.TimeColumn >= 0 && (source.IsMissing(group, source.TimeColumn) || string.IsNullOrWhiteSpace(group));
                    if (badId) { missingId++; issues.Add(new(source.Key, r, source.IdColumn, MatchIssueKind.MissingId, id, group, "This record cannot be matched until its ID is resolved.", Entity: entity)); }
                    if (badEntity) { missingEntity++; issues.Add(new(source.Key, r, source.EntityColumn, MatchIssueKind.MissingEntity, id, group, "This record cannot be matched until its additional key is resolved.", Entity: entity)); }
                    if (badGroup) { missingGroup++; issues.Add(new(source.Key, r, source.TimeColumn, MatchIssueKind.MissingGrouping, id, group, "This record cannot be matched until its grouping value is resolved.", Entity: entity)); }
                    if (badId || badEntity || badGroup) continue;
                    completeIds.Add(id);
                    if (!keys.TryGetValue((id, entity, group), out var records)) keys[(id, entity, group)] = records = [];
                    records.Add(r);
                }
            }
            var duplicates = keys.Where(p => p.Value.Count > 1).ToArray();
            foreach (var pair in duplicates)
                foreach (int row in pair.Value)
                    issues.Add(new(source.Key, row, source.TimeColumn >= 0 ? source.TimeColumn : source.EntityColumn >= 0 ? source.EntityColumn : source.IdColumn, MatchIssueKind.DuplicateKey, pair.Key.Id, pair.Key.Group, $"{pair.Value.Count:N0} records share the configured ID/additional-key/time key. No records are discarded or multiplied.", Entity: pair.Key.Entity));
            var report = new FileMatchReport
            {
                SourceKey = source.Key, File = source.Name, Rows = source.Rows.Count, Configured = configured,
                IdColumn = source.IdColumn >= 0 && source.IdColumn < source.Fields.Count ? source.Fields[source.IdColumn].Name : "Choose…",
                EntityColumn = source.EntityColumn >= 0 && source.EntityColumn < source.Fields.Count ? source.Fields[source.EntityColumn].Name : "None — shared across entities",
                GroupingColumn = source.TimeColumn >= 0 && source.TimeColumn < source.Fields.Count ? source.Fields[source.TimeColumn].Name : "None — ID / wide columns",
                DistinctIds = completeIds.Count, DistinctKeys = keys.Count, MissingIdRows = missingId, MissingEntityRows = missingEntity, MissingGroupingRows = missingGroup,
                DuplicateKeys = duplicates.Length, DuplicateRows = duplicates.Sum(d => d.Value.Count), ExtraDuplicateRows = duplicates.Sum(d => d.Value.Count - 1),
                IdReason = source.IdReason, EntityReason = source.EntityReason, GroupingReason = source.GroupReason, CompleteIds = completeIds
            };
            report.Issues.AddRange(issues); reports.Add(report);
        }
        foreach (var report in reports.Where(r => r.Configured))
        {
            var source = session.Sources.Single(s => s.Key == report.SourceKey);
            foreach (var other in reports.Where(r => r.SourceKey != report.SourceKey && r.Configured))
            {
                int shared = report.CompleteIds.Count(other.CompleteIds.Contains);
                report.SharedIds.Add(new(other.SourceKey, other.File, shared, report.CompleteIds.Count, other.CompleteIds.Count));
                for (int r = 0; r < source.Rows.Count; r++)
                {
                    string id = source.Rows[r][source.IdColumn];
                    if (!source.Keeps(r) || !report.CompleteIds.Contains(id) || other.CompleteIds.Contains(id)) continue;
                    if (source.EntityColumn >= 0 && (source.IsMissing(source.Rows[r][source.EntityColumn], source.EntityColumn) || string.IsNullOrWhiteSpace(source.Rows[r][source.EntityColumn]))) continue;
                    if (source.TimeColumn >= 0 && (source.IsMissing(source.Rows[r][source.TimeColumn], source.TimeColumn) || string.IsNullOrWhiteSpace(source.Rows[r][source.TimeColumn]))) continue;
                    report.Issues.Add(new(source.Key, r, source.IdColumn, MatchIssueKind.UnmatchedId, id, source.TimeColumn < 0 ? "" : source.Rows[r][source.TimeColumn], "No complete key with this ID in " + other.File + ".", other.SourceKey, source.EntityColumn < 0 ? "" : source.Rows[r][source.EntityColumn]));
                }
            }
        }
        return reports;
    }
}
