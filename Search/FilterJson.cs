using System.Text.Json;

namespace DapperSearchDemo.Search;

/// <summary>
/// Reads a filter stored as JSON (tenant config, saved prefilter, grid request) into a <see cref="FilterNode"/>.
/// No parser or grammar needed: the structure *is* the syntax tree.
///
///   { "and": [ ... ] }  /  { "or": [ ... ] }
///   { "field": "OriginCode", "op": "eq", "value": "DKAAR" }
///   { "field": "Containers.ContainerNumber", "op": "contains", "value": "MSCU" }
///   { "field": "Containers", "op": "any", "where": { ...filter on the container... } }
///   { "field": "Containers", "op": "none" }
///   { "field": "ContainerNumber", "op": "in", "value": ["A", "B"] }
/// </summary>
public static class FilterJson
{
    public static FilterNode Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Read(doc.RootElement);
    }

    private static FilterNode Read(JsonElement e)
    {
        if (e.TryGetProperty("and", out var and)) return new FilterGroup(LogicalOperator.And, and.EnumerateArray().Select(Read).ToList());
        if (e.TryGetProperty("or", out var or)) return new FilterGroup(LogicalOperator.Or, or.EnumerateArray().Select(Read).ToList());

        var field = e.GetProperty("field").GetString()!;
        var op = Enum.Parse<Op>(e.GetProperty("op").GetString()!, ignoreCase: true);

        object? value = e.TryGetProperty("where", out var where) ? Read(where)
                      : e.TryGetProperty("value", out var v) ? ToClr(v)
                      : null;

        return new FilterCondition(field, op, value);
    }

    private static object? ToClr(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => v.EnumerateArray().Select(ToClr).ToList(),
        _ => throw new NotSupportedException($"Unsupported JSON value: {v}"),
    };
}
