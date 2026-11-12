using System.Text.RegularExpressions;

namespace WideLong;

public static class SchemaNaming
{
    public static string IdName(Session session) => session.Sources.Where(s => s.IdColumn >= 0).Select(s => s.Fields[s.IdColumn].Name).FirstOrDefault() ?? "ID";
    public static bool HasEntity(Session session) => session.Sources.Any(s=>s.EntityColumn>=0);
    public static string EntityName(Session session)
    {
        string name=session.Sources.Where(s=>s.EntityColumn>=0).Select(s=>s.Fields[s.EntityColumn].Name).FirstOrDefault()??"entity";
        return name.Equals(IdName(session),StringComparison.OrdinalIgnoreCase)?name+"_entity":name;
    }
    public static string GroupName(Session session)
    {
        string? name = session.Sources.Where(s => s.TimeColumn >= 0).Select(s => s.Fields[s.TimeColumn].Name).FirstOrDefault();
        var labels = session.Sources.SelectMany(s => s.Fields).Where(f => f.Keep && f.Measure.Length > 0).Select(f => f.Occasion).ToArray();
        name ??= new[] { "term", "wave", "month", "visit", "trial", "year" }.FirstOrDefault(prefix => labels.Any(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) ?? "time";
        return name.Equals(IdName(session), StringComparison.OrdinalIgnoreCase) || HasEntity(session)&&name.Equals(EntityName(session),StringComparison.OrdinalIgnoreCase) ? name + "_group" : name;
    }
}

public static class KeyRecommendations
{
    private static string Normal(string name) => Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
    public static void Recommend(Session session)
    {
        foreach (var source in session.Sources)
        {
            if (!source.IdChosen)
            {
                var candidates = source.Fields.Select((f, c) =>
                {
                    string name = Normal(f.Name);
                    int score = name is "id" or "id_number" or "idnumber" or "responseid" || name.EndsWith("_id") ? 100 : name.EndsWith("_code") || name is "department" or "account" or "device" or "product" ? 55 : 0;
                    if (name is "row_number" or "row_id" or "index" or "record_number") score = 0;
                    var values = source.Rows.Select(r => r[c]).ToHashSet(StringComparer.Ordinal);
                    if (values.Any(v => source.IsMissing(v, c) || string.IsNullOrWhiteSpace(v))) score = 0;
                    if (score > 0)
                    {
                        if (values.Count == source.Rows.Count) score += 15;
                        if (session.Sources.Any(other => other != source && other.Fields.Select((field, index) => (field, index)).Any(x => (Normal(x.field.Name) == name || Normal(x.field.Name).EndsWith("_id")) && other.Rows.Any(r => values.Contains(r[x.index]))))) score += 25;
                    }
                    return (Column: c, Score: score, Unique: values.Count == source.Rows.Count);
                }).OrderByDescending(x => x.Score).FirstOrDefault();
                source.IdColumn = candidates.Score > 0 ? candidates.Column : -1;
                source.IdReason = source.IdColumn < 0 ? "Choose the column containing the ID; no confident recommendation was found." : $"Recommended {source.Fields[source.IdColumn].Name}: ID-like name, complete values" + (candidates.Unique ? ", and one value per source row." : ". Repeated IDs require a grouping column.");
            }
            if (!source.EntityChosen && source.IdColumn>=0)
            {
                bool repeatedIds=source.Rows.Select(r=>r[source.IdColumn]).Distinct(StringComparer.Ordinal).Count()<source.Rows.Count;
                var entity=source.Fields.Select((f,c)=>
                {
                    string n=Normal(f.Name);
                    int score=n is "person" or "role" or "person_type" or "respondent_type" or "informant_type" or "member_type" or "entity" ? 120 : n is "site" or "device" or "unit" ? 50 : 0;
                    int categories=source.Rows.Select(r=>r[c]).Distinct(StringComparer.Ordinal).Count();
                    if(c==source.IdColumn||source.Rows.Any(r=>source.IsMissing(r[c],c)||string.IsNullOrWhiteSpace(r[c]))||categories==0)score=0;
                    if(n=="person"&&categories>12&&!repeatedIds)score=0;
                    if(score==50&&!repeatedIds)score=0;
                    return (Column:c,Score:score);
                }).OrderByDescending(x=>x.Score).FirstOrDefault();
                source.EntityColumn=entity.Score>0?entity.Column:-1;
                source.EntityReason=source.EntityColumn>=0?$"Recommended {source.Fields[source.EntityColumn].Name} as an additional matching key. Different roles/entities remain separate at the same time point.":"No additional key recommended. Choose a role, site or device column if ID + time repeats for different entities.";
            }
            if (!source.GroupChosen && source.IdColumn >= 0)
            {
                bool repeated = source.Rows.Select(r => r[source.IdColumn]).Distinct(StringComparer.Ordinal).Count() < source.Rows.Count;
                var candidate = source.Fields.Select((f, c) =>
                {
                    string name = Normal(f.Name);
                    int score = Regex.IsMatch(name, "(^|_)(time|visit|month|term|wave|period|trial|occasion|timepoint|order_number|event_name)($|_)") ? 100 : name.Contains("date") || name.Contains("timestamp") ? 65 : name is "location" or "site" or "scenario" ? 50 : 0;
                    if (score == 0 && repeated && source.Rows.Count > 1 && source.Rows.All(r => DateTime.TryParseExact(r[c], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))) score = 45;
                    if (c == source.IdColumn || c==source.EntityColumn || name.Contains("birth") || name == "dob" || (!repeated && score < 100)) score = 0;
                    if (source.Rows.Any(r => source.IsMissing(r[c], c) || string.IsNullOrWhiteSpace(r[c]))) score = 0;
                    bool unique=source.Rows.Select(r=>(r[source.IdColumn],Entity:source.EntityColumn<0?"":r[source.EntityColumn],Time:r[c])).Distinct().Count()==source.Rows.Count;
                    if(!unique&&score<100)score=0;
                    return (Column: c, Score: score);
                }).OrderByDescending(x => x.Score).FirstOrDefault();
                source.TimeColumn = candidate.Score > 0 ? candidate.Column : -1;
                int duplicates=source.TimeColumn<0?0:source.Rows.Count-source.Rows.Select(r=>(r[source.IdColumn],Entity:source.EntityColumn<0?"":r[source.EntityColumn],Time:r[source.TimeColumn])).Distinct().Count();
                source.GroupReason = source.TimeColumn < 0 ? "No time/grouping column recommended. Choose one for long files, or use mapped repeated columns for wide files." : $"Recommended {source.Fields[source.TimeColumn].Name} as time/grouping."+(duplicates==0?" The full matching key is unique.":$" {duplicates:N0} extra records still repeat the full key; inspect them in Review matching.");
            }
            if (source.IdColumn >= 0) source.Fields[source.IdColumn].Keep = true;
            if (source.TimeColumn >= 0) source.Fields[source.TimeColumn].Keep = true;
            if(source.EntityColumn>=0) source.Fields[source.EntityColumn].Keep=true;
        }
    }
}
