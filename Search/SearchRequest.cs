namespace DapperSearchDemo.Search;

/// <summary>
/// What a grid / search box / saved prefilter would send. Field names are *logical* paths
/// (e.g. "Containers.ContainerNumber"), never SQL. The builder only accepts paths that are
/// registered in the entity's <see cref="SearchMap"/>.
/// </summary>
public sealed class SearchRequest
{
    public FilterNode? Filter { get; init; }

    /// <summary>
    /// Optional Dynamic-LINQ style criteria string, as stored in tenant configuration, e.g.
    /// <c>Containers.Where(ContainerType == "40HC").Any() &amp;&amp; Consolidation.TransportMode == "SEA"</c>.
    /// Combined with <see cref="Filter"/> using AND.
    /// </summary>
    public string? Criteria { get; init; }
    public List<SortSpec> Sort { get; init; } = new();
    public int Skip { get; init; }
    public int Take { get; init; } = 50;
}

public sealed record SortSpec(string Field, bool Descending = false);

/// <summary>Filter tree: either a group (AND/OR of children) or a single condition.</summary>
public abstract record FilterNode;

public sealed record FilterGroup(LogicalOperator Logic, IReadOnlyList<FilterNode> Children) : FilterNode;

public sealed record FilterCondition(string Field, Op Operator, object? Value = null) : FilterNode;

public enum LogicalOperator { And, Or }

public enum Op
{
    Eq, Neq, Contains, StartsWith, In, Gt, Lt, IsNull, IsNotNull,
    /// <summary>Collection only: "has at least one element" (no value needed).</summary>
    Any,
    /// <summary>Collection only: "has no elements".</summary>
    None,
}

/// <summary>Small helpers so code-built filters read nicely.</summary>
public static class F
{
    public static FilterGroup And(params FilterNode[] c) => new(LogicalOperator.And, c);
    public static FilterGroup Or(params FilterNode[] c) => new(LogicalOperator.Or, c);
    public static FilterCondition Where(string field, Op op, object? value = null) => new(field, op, value);
}
