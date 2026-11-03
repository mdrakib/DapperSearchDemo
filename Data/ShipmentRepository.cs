using System.Data;
using Dapper;
using DapperSearchDemo.Domain;
using DapperSearchDemo.Search;

namespace DapperSearchDemo.Data;

public sealed record SearchResult<T>(int Total, IReadOnlyList<T> Items, string Sql);

public sealed class ShipmentRepository(IDbConnection db)
{
    private const string Alias = "s";

    /// <summary>
    /// Search shipments with a dynamic filter/sort/page, optionally loading Containers.
    /// Round-trips: 1 for count + page, +1 if Containers are requested (no N+1).
    /// </summary>
    public SearchResult<Shipment> Search(SearchRequest request, UserContext user, bool includeContainers = false)
    {
        var builder = new SqlFilterBuilder();

        // Security filter is applied here, not by the caller, so no search can skip it.
        var security = BuildDivisionFilter(builder, user);
        var userFilter = builder.BuildWhere(ShipmentSearchMap.Shipment, Alias, request.Filter);
        if (!string.IsNullOrWhiteSpace(request.Criteria))
            userFilter += " AND " + new CriteriaTranslator(builder).Translate(ShipmentSearchMap.Shipment, Alias, request.Criteria);
        var orderBy = builder.BuildOrderBy(ShipmentSearchMap.Shipment, Alias, request.Sort, $"{Alias}.ID");

        builder.Parameters.Add("@skip", request.Skip);
        builder.Parameters.Add("@take", request.Take);

        var sql = $"""
            SELECT COUNT(*)
            FROM dbo.JobShipment {Alias}
            WHERE {security} AND {userFilter};

            SELECT {Alias}.ID, {Alias}.UniqueConsignRef, {Alias}.HouseBill, {Alias}.ShipmentType,
                   {Alias}.OriginCode, {Alias}.DestinationCode, {Alias}.ConsolidationID, {Alias}.CustomerDivision
            FROM dbo.JobShipment {Alias}
            WHERE {security} AND {userFilter}
            ORDER BY {orderBy}
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            """;

        using var grid = db.QueryMultiple(sql, builder.Parameters);
        var total = grid.ReadSingle<int>();
        var items = grid.Read<Shipment>().ToList();

        if (includeContainers && items.Count > 0)
            LoadContainers(items);

        return new SearchResult<Shipment>(total, items, sql);
    }

    /// <summary>
    /// Loads Shipment.Containers for a whole page in one query, reusing the same SQL definition
    /// that the filter used (the equivalent of EF projecting a translated navigation).
    /// </summary>
    private void LoadContainers(List<Shipment> shipments)
    {
        var sql = $"""
            SELECT {Alias}.ID AS OwnerShipmentID, c.*
            FROM dbo.JobShipment {Alias}
            CROSS APPLY ({ShipmentSearchMap.ContainersSql(Alias)}) AS c
            WHERE {Alias}.ID IN @ids
            ORDER BY c.ContainerNumber;
            """;

        var rows = db.Query<ShipmentContainerRow>(sql, new { ids = shipments.Select(s => s.ID).ToList() });
        var byShipment = rows.ToLookup(r => r.OwnerShipmentID);
        foreach (var s in shipments)
            s.Containers = byShipment[s.ID].Cast<JobContainer>().ToList();
    }

    /// <summary>Mirrors DbFactory: ctx.Filter&lt;Shipment&gt;(q => q.Where(x => (allowEmpty &amp;&amp; IsNullOrEmpty(x.CustomerDivision)) || divisions.Contains(x.CustomerDivision))).</summary>
    private static string BuildDivisionFilter(SqlFilterBuilder builder, UserContext user)
    {
        var parts = new List<string>();
        if (user.ActiveCustomerDivisions.Count > 0)
            parts.Add($"{Alias}.CustomerDivision IN {builder.AddParameter(user.ActiveCustomerDivisions)}");
        if (user.IncludeUnassignedDivision)
            parts.Add($"({Alias}.CustomerDivision IS NULL OR {Alias}.CustomerDivision = '')");
        return parts.Count == 0 ? "1 = 0" : "(" + string.Join(" OR ", parts) + ")";
    }
}
