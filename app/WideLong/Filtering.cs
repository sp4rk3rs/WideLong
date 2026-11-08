using System.Globalization;

namespace WideLong;

public enum FilterOperator { Equal, NotEqual, Contains, NotContains, Greater, GreaterEqual, Less, LessEqual, Between, IsMissing, IsNotMissing }
public sealed record RowFilterRule(string Key, string Name, ValueKind Kind, FilterOperator Operator, string Value = "", string UpperValue = "");
public sealed class RowFilterSpec
{
    public List<RowFilterRule> Rules { get; set; } = [];
    public bool MatchAll { get; set; } = true;
    public RowFilterSpec Copy() => new() { Rules = [.. Rules], MatchAll = MatchAll };
}
public sealed record CompiledFilterRule(int Column, OutputField Field, RowFilterRule Rule);

/// <summary>Pure, deterministic row predicates. Missing never compares equal or unequal to an ordinary value.</summary>
public static class Filtering
{
    public static string Label(FilterOperator op) => op switch
    {
        FilterOperator.Equal => "equals", FilterOperator.NotEqual => "does not equal",
        FilterOperator.Contains => "contains", FilterOperator.NotContains => "does not contain",
        FilterOperator.Greater => "greater than", FilterOperator.GreaterEqual => "at least",
        FilterOperator.Less => "less than", FilterOperator.LessEqual => "at most",
        FilterOperator.Between => "between (inclusive)", FilterOperator.IsMissing => "is missing", _ => "is not missing"
    };
    public static bool NeedsValue(FilterOperator op) => op is not (FilterOperator.IsMissing or FilterOperator.IsNotMissing);
    public static FilterOperator[] Operators(ValueKind kind) => kind switch
    {
        ValueKind.Number or ValueKind.Date => [FilterOperator.Equal, FilterOperator.NotEqual, FilterOperator.Greater, FilterOperator.GreaterEqual, FilterOperator.Less, FilterOperator.LessEqual, FilterOperator.Between, FilterOperator.IsMissing, FilterOperator.IsNotMissing],
        ValueKind.Boolean => [FilterOperator.Equal, FilterOperator.NotEqual, FilterOperator.IsMissing, FilterOperator.IsNotMissing],
        _ => [FilterOperator.Equal, FilterOperator.NotEqual, FilterOperator.Contains, FilterOperator.NotContains, FilterOperator.IsMissing, FilterOperator.IsNotMissing]
    };
    public static string Summary(RowFilterSpec? spec) => spec == null || spec.Rules.Count == 0 ? "No filter rules" : $"{spec.Rules.Count} filter rule{(spec.Rules.Count == 1 ? "" : "s")} · {(spec.MatchAll ? "all must match" : "any may match")}";
    private static decimal Number(string value) => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : throw new InvalidOperationException("Enter a Number using a decimal point, for example 12.5.");
    private static string NormalValue(string value, ValueKind kind) => kind switch
    {
        ValueKind.Number => Number(value).ToString(CultureInfo.InvariantCulture),
        ValueKind.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : throw new InvalidOperationException("Enter a Date as yyyy-MM-dd, for example 2025-01-31."),
        ValueKind.Boolean => Values.Convert(value, ValueKind.Boolean) is { Length: > 0 } boolean ? boolean : throw new InvalidOperationException("Enter true or false for a Boolean rule."),
        _ => value
    };
    public static RowFilterRule Normalize(RowFilterRule rule)
    {
        if (rule.Key.Length == 0) throw new InvalidOperationException("Choose a variable for each filter rule.");
        if (!Operators(rule.Kind).Contains(rule.Operator)) throw new InvalidOperationException($"{Label(rule.Operator)} is not available for {rule.Kind} variables.");
        if (!NeedsValue(rule.Operator)) return rule with { Value = "", UpperValue = "" };
        string first = NormalValue(rule.Value, rule.Kind);
        string upper = rule.Operator == FilterOperator.Between ? NormalValue(rule.UpperValue, rule.Kind) : "";
        if (rule.Operator == FilterOperator.Between && Compare(first, upper, rule.Kind) > 0) throw new InvalidOperationException("The first Between value must be no greater than the second value.");
        return rule with { Value = first, UpperValue = upper };
    }
    public static List<CompiledFilterRule> Compile(IReadOnlyList<OutputField> fields, RowFilterSpec? spec)
    {
        if (spec == null) return [];
        var keys = fields.Select(Presentation.Identity).ToArray();
        return spec.Rules.Select(rule =>
        {
            int column = Array.IndexOf(keys, rule.Key);
            if (column < 0) throw new InvalidOperationException($"The filter variable '{rule.Name}' is unavailable in this layout or was unchecked. Edit or clear Filters before viewing or exporting this shape.");
            if (fields[column].Kind != rule.Kind) throw new InvalidOperationException($"The type of filter variable '{rule.Name}' changed from {rule.Kind} to {fields[column].Kind}. Edit its filter rule before continuing.");
            return new CompiledFilterRule(column, fields[column], Normalize(rule));
        }).ToList();
    }
    private static int Compare(string a, string b, ValueKind kind) => kind == ValueKind.Number ? Number(a).CompareTo(Number(b)) : string.CompareOrdinal(a, b);
    private static bool Match(Cell cell, CompiledFilterRule compiled)
    {
        var rule = compiled.Rule;
        if (rule.Operator == FilterOperator.IsMissing) return cell.Missing;
        if (rule.Operator == FilterOperator.IsNotMissing) return !cell.Missing;
        if (cell.Missing) return false;
        if (rule.Operator == FilterOperator.Contains) return cell.Value.Contains(rule.Value, StringComparison.Ordinal);
        if (rule.Operator == FilterOperator.NotContains) return !cell.Value.Contains(rule.Value, StringComparison.Ordinal);
        string value = rule.Kind is ValueKind.Date or ValueKind.Boolean ? NormalValue(cell.Value, rule.Kind) : cell.Value;
        int comparison = Compare(value, rule.Value, rule.Kind);
        return rule.Operator switch
        {
            FilterOperator.Equal => comparison == 0, FilterOperator.NotEqual => comparison != 0,
            FilterOperator.Greater => comparison > 0, FilterOperator.GreaterEqual => comparison >= 0,
            FilterOperator.Less => comparison < 0, FilterOperator.LessEqual => comparison <= 0,
            FilterOperator.Between => comparison >= 0 && Compare(value, rule.UpperValue, rule.Kind) <= 0,
            _ => false
        };
    }
    public static View Apply(View input, RowFilterSpec? spec)
    {
        var rules = Compile(input.Fields, spec);
        if (rules.Count == 0) return input;
        return new View { Fields = input.Fields, Notes = [.. input.Notes], Rows = input.Rows.Where(row => spec!.MatchAll ? rules.All(r => Match(row[r.Column], r)) : rules.Any(r => Match(row[r.Column], r))).ToList() };
    }
    private static void ValidateCodeLiteral(RowFilterRule rule)
    {
        if (rule.Kind != ValueKind.Number || !NeedsValue(rule.Operator)) return;
        foreach (string value in new[] { rule.Value, rule.UpperValue }.Where(v => v.Length > 0))
            if (value.Replace("-", "").Replace(".", "").TrimStart('0').Length > 15) throw new InvalidOperationException($"The numeric filter on '{rule.Name}' exceeds the precision supported by generated R/SAS code. Use a value with at most 15 significant digits.");
    }
    private static void ValidateSasLiteral(RowFilterRule rule)
    {
        if (!NeedsValue(rule.Operator)) return;
        foreach (string value in new[] { rule.Value, rule.UpperValue }.Where(v => v.Length > 0))
        {
            if (value.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new InvalidOperationException($"SAS cannot reproduce a multiline/control-character filter value for '{rule.Name}'. Choose R for this filter.");
            if (rule.Kind == ValueKind.Text && value != value.Trim()) throw new InvalidOperationException($"SAS cannot reproduce leading/trailing-whitespace distinctions in the filter on '{rule.Name}'. Choose R for this exact Text filter.");
        }
    }
    /// <summary>Insert after constructing character-valued `prepared`, before search/sorting and final typing.</summary>
    public static string RExpression(IReadOnlyList<OutputField> fields, RowFilterSpec? spec)
    {
        var rules = Compile(fields, spec);
        if (rules.Count == 0) return "rep(TRUE, nrow(prepared))";
        string Predicate(CompiledFilterRule compiled)
        {
            var rule = compiled.Rule; ValidateCodeLiteral(rule);
            string raw = $"prepared[[{compiled.Column + 1}]]";
            if (rule.Operator == FilterOperator.IsMissing) return $"is.na({raw})";
            if (rule.Operator == FilterOperator.IsNotMissing) return $"!is.na({raw})";
            string value = rule.Kind == ValueKind.Number ? $"local({{ z <- {raw}; n <- suppressWarnings(as.numeric(z)); if (any(!is.na(z) & !is.finite(n))) stop({Recipe.RQuote("Invalid numeric value in filter variable '" + rule.Name + "'.")}); n }})" : raw;
            string Literal(string text) => rule.Kind == ValueKind.Number ? text : Recipe.RQuote(text);
            string comparison = rule.Operator switch
            {
                FilterOperator.Contains => $"grepl({Literal(rule.Value)}, {raw}, fixed=TRUE)",
                FilterOperator.NotContains => $"!grepl({Literal(rule.Value)}, {raw}, fixed=TRUE)",
                FilterOperator.Between => $"({value} >= {Literal(rule.Value)} & {value} <= {Literal(rule.UpperValue)})",
                _ => value + " " + (rule.Operator switch { FilterOperator.Equal => "==", FilterOperator.NotEqual => "!=", FilterOperator.Greater => ">", FilterOperator.GreaterEqual => ">=", FilterOperator.Less => "<", _ => "<=" }) + " " + Literal(rule.Value)
            };
            return $"(!is.na({raw}) & {comparison})";
        }
        return string.Join(spec!.MatchAll ? " & " : " | ", rules.Select(r => "(" + Predicate(r) + ")"));
    }
    /// <summary>SAS character blanks cannot reproduce genuine empty Text values for predicate evaluation.</summary>
    public static void EnsureSasCompatible(View input, RowFilterSpec? spec)
    {
        foreach (var rule in Compile(input.Fields, spec))
        {
            ValidateSasLiteral(rule.Rule);
            if (rule.Field.Kind == ValueKind.Text && input.Rows.Any(row => !row[rule.Column].Missing && row[rule.Column].Value.Length == 0)) throw new InvalidOperationException($"SAS cannot distinguish empty Text from missing values in filtered variable '{rule.Field.Name}'. Choose R for this filter, or explicitly classify empty cells as missing.");
        }
    }
    /// <summary>Insert in the pretyping SAS DATA step, where each output column is _dh_o1, _dh_o2, ...</summary>
    public static string SasExpression(IReadOnlyList<OutputField> fields, RowFilterSpec? spec)
    {
        var rules = Compile(fields, spec);
        if (rules.Count == 0) return "1";
        string Predicate(CompiledFilterRule compiled)
        {
            var rule = compiled.Rule; ValidateCodeLiteral(rule); ValidateSasLiteral(rule);
            string raw = "_dh_o" + (compiled.Column + 1);
            if (rule.Operator == FilterOperator.IsMissing) return $"missing({raw})";
            if (rule.Operator == FilterOperator.IsNotMissing) return $"not missing({raw})";
            if (rule.Kind == ValueKind.Text && rule.Value.Length == 0) throw new InvalidOperationException("SAS cannot reproduce a Text filter against a genuine empty string separately from missing values. Choose R or use an explicit missing-value rule.");
            string value = rule.Kind == ValueKind.Number ? $"input({raw}, ?? best32.)" : raw;
            string Literal(string text) => rule.Kind == ValueKind.Number ? text : Recipe.SasQuote(text);
            string comparison = rule.Operator switch
            {
                FilterOperator.Contains => $"index({raw}, {Literal(rule.Value)}) > 0",
                FilterOperator.NotContains => $"index({raw}, {Literal(rule.Value)}) = 0",
                FilterOperator.Between => $"({value} >= {Literal(rule.Value)} and {value} <= {Literal(rule.UpperValue)})",
                _ => value + " " + (rule.Operator switch { FilterOperator.Equal => "=", FilterOperator.NotEqual => "ne", FilterOperator.Greater => ">", FilterOperator.GreaterEqual => ">=", FilterOperator.Less => "<", _ => "<=" }) + " " + Literal(rule.Value)
            };
            return $"(not missing({raw}) and {comparison})";
        }
        return string.Join(spec!.MatchAll ? " and " : " or ", rules.Select(r => "(" + Predicate(r) + ")"));
    }
}
