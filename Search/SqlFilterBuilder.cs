using System.Collections;
using System.Text;
using Dapper;

namespace DapperSearchDemo.Search;

/// <summary>
/// Turns a <see cref="FilterNode"/> tree into a parameterised SQL predicate and an ORDER BY.
/// This is the piece EF Core's LINQ translator gives you for free; with Dapper you own it.
///
/// Rules that keep it safe:
///  * field names are resolved only through the <see cref="SearchMap"/> whitelist;
///  * values are never concatenated into SQL, they always become Dapper parameters;
///  * every alias / parameter name is generated here, never taken from input.
/// </summary>
public sealed class SqlFilterBuilder
{
    private int _paramIndex;
    private int _aliasIndex;

    public DynamicParameters Parameters { get; } = new();

    public string BuildWhere(SearchMap map, string alias, FilterNode? node) =>
        node is null ? "1 = 1" : Visit(map, alias, node);

    public string BuildOrderBy(SearchMap map, string alias, IEnumerable<SortSpec> sort, string tieBreaker)
    {
        var parts = new List<string>();
        foreach (var s in sort)
        {
            if (!map.TryGetScalar(s.Field, out var field) || !field.Sortable)
                throw new UnknownFieldException(map.EntityName, s.Field);
            parts.Add($"{field.Sql(alias)} {(s.Descending ? "DESC" : "ASC")}");
        }
        parts.Add(tieBreaker); // stable paging
        return string.Join(", ", parts);
    }

    /// <summary>Unique table alias for sub-queries (shared with <see cref="CriteriaTranslator"/>).</summary>
    public string NextAlias(string prefix = "c") => $"{prefix}{_aliasIndex++}";

    public string AddParameter(object? value)
    {
        var name = $"@p{_paramIndex++}";
        Parameters.Add(name, value);
        return name;
    }

    private string Visit(SearchMap map, string alias, FilterNode node) => node switch
    {
        FilterGroup g when g.Children.Count == 0 => "1 = 1",
        FilterGroup g => "(" + string.Join(g.Logic == LogicalOperator.And ? " AND " : " OR ",
                             g.Children.Select(c => Visit(map, alias, c))) + ")",
        FilterCondition c => VisitCondition(map, alias, c),
        _ => throw new NotSupportedException(node.GetType().Name),
    };

    private string VisitCondition(SearchMap map, string alias, FilterCondition c)
    {
        // "Containers.ContainerNumber" -> head "Containers", tail "ContainerNumber"
        var dot = c.Field.IndexOf('.');
        var head = dot < 0 ? c.Field : c.Field[..dot];
        var tail = dot < 0 ? null : c.Field[(dot + 1)..];

        if (map.TryGetCollection(head, out var collection))
            return VisitCollection(collection, alias, tail, c);

        if (tail is null && map.TryGetScalar(head, out var scalar))
            return Compare(scalar.Sql(alias), c.Operator, c.Value);

        throw new UnknownFieldException(map.EntityName, c.Field);
    }

    /// <summary>
    /// Collection conditions become EXISTS over the collection's correlated source query.
    ///   Containers.ContainerNumber Contains 'X'   => EXISTS (... WHERE c.ContainerNumber LIKE '%X%')
    ///   Containers Any { sub filter }             => EXISTS (... WHERE sub filter)   (same element)
    ///   Containers None                           => NOT EXISTS (...)
    /// Works recursively, so collections inside collections are supported too.
    /// </summary>
    private string VisitCollection(CollectionField collection, string outerAlias, string? tail, FilterCondition c)
    {
        var inner = $"c{_aliasIndex++}";
        var from = $"SELECT 1 FROM ({collection.Source(outerAlias)}) AS {inner}";

        if (tail is null)
        {
            switch (c.Operator)
            {
                case Op.None:
                    return $"NOT EXISTS ({from})";
                case Op.Any when c.Value is null:
                    return $"EXISTS ({from})";
                case Op.Any when c.Value is FilterNode sub:
                    return $"EXISTS ({from} WHERE {Visit(collection.ElementMap, inner, sub)})";
                default:
                    throw new NotSupportedException($"{c.Operator} is not valid on collection '{collection.Name}'.");
            }
        }

        var elementCondition = new FilterCondition(tail, c.Operator, c.Value);
        return $"EXISTS ({from} WHERE {VisitCondition(collection.ElementMap, inner, elementCondition)})";
    }

    private string Compare(string column, Op op, object? value) => op switch
    {
        Op.Eq when value is null => $"{column} IS NULL",
        Op.Eq => $"{column} = {AddParameter(value)}",
        Op.Neq when value is null => $"{column} IS NOT NULL",
        Op.Neq => $"({column} <> {AddParameter(value)} OR {column} IS NULL)",
        Op.Contains => $"{column} LIKE {AddParameter("%" + EscapeLike(value) + "%")} ESCAPE '\\'",
        Op.StartsWith => $"{column} LIKE {AddParameter(EscapeLike(value) + "%")} ESCAPE '\\'",
        Op.Gt => $"{column} > {AddParameter(value)}",
        Op.Lt => $"{column} < {AddParameter(value)}",
        Op.IsNull => $"{column} IS NULL",
        Op.IsNotNull => $"{column} IS NOT NULL",
        // Dapper expands a list parameter into (@p0_1, @p0_2, ...)
        Op.In when value is IEnumerable and not string =>
            ((IEnumerable)value).Cast<object>().Any() ? $"{column} IN {AddParameter(value)}" : "1 = 0",
        _ => throw new NotSupportedException($"Operator {op} with value '{value}' on {column}."),
    };

    internal static string EscapeLike(object? value)
    {
        var s = Convert.ToString(value) ?? string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '\\' or '%' or '_' or '[') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
