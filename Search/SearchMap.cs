namespace DapperSearchDemo.Search;

/// <summary>
/// Whitelist of searchable/sortable fields for one entity. Plays the same role as the
/// eDC translation classes (e.g. ShipmentTranslation): a logical property name maps to SQL.
///
/// Every SQL fragment is a function of the *table alias* it is evaluated against, so the same
/// definition can be reused in WHERE, ORDER BY, EXISTS sub-queries and CROSS APPLY loading.
/// </summary>
public sealed class SearchMap
{
    private readonly Dictionary<string, ScalarField> _scalars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CollectionField> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ReferenceField> _references = new(StringComparer.OrdinalIgnoreCase);

    public SearchMap(string entityName) => EntityName = entityName;

    public string EntityName { get; }

    /// <summary>A plain column, e.g. Scalar("ShipmentNumber", a => $"{a}.UniqueConsignRef").</summary>
    public SearchMap Scalar(string name, Func<string, string> sql, bool sortable = true)
    {
        _scalars[name] = new ScalarField(name, sql, sortable);
        return this;
    }

    /// <summary>
    /// A computed collection, e.g. Shipment.Containers. <paramref name="source"/> returns a
    /// SELECT correlated to the outer alias; <paramref name="elementMap"/> describes the
    /// columns of the rows it returns.
    /// </summary>
    public SearchMap Collection(string name, Func<string, string> source, SearchMap elementMap)
    {
        _collections[name] = new CollectionField(name, source, elementMap);
        return this;
    }

    /// <summary>
    /// A single related row (navigation), e.g. Shipment.Consolidation. <paramref name="source"/> returns a
    /// SELECT of at most one row correlated to the outer alias.
    /// </summary>
    public SearchMap Reference(string name, Func<string, string> source, Func<SearchMap> target)
    {
        _references[name] = new ReferenceField(name, source, target);
        return this;
    }

    public bool TryGetReference(string name, out ReferenceField field) => _references.TryGetValue(name, out field!);

    public bool TryGetScalar(string name, out ScalarField field) => _scalars.TryGetValue(name, out field!);
    public bool TryGetCollection(string name, out CollectionField field) => _collections.TryGetValue(name, out field!);

    public CollectionField GetCollection(string name) =>
        TryGetCollection(name, out var c) ? c : throw new UnknownFieldException(EntityName, name);
}

public sealed record ScalarField(string Name, Func<string, string> Sql, bool Sortable);

public sealed record CollectionField(string Name, Func<string, string> Source, SearchMap ElementMap);

/// <summary>Target is lazy so maps can reference each other (Shipment -> Consolidation -> Shipments ...).</summary>
public sealed record ReferenceField(string Name, Func<string, string> Source, Func<SearchMap> Target);

public sealed class UnknownFieldException(string entity, string field)
    : Exception($"'{field}' is not a searchable field of {entity}.");
