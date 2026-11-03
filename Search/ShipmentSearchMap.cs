namespace DapperSearchDemo.Search;

/// <summary>
/// The Dapper equivalent of eDC's ShipmentTranslation.cs.
///
/// Original EF translation (eDC.Infrastructure/DataAccess/Translations/Entity/ShipmentTranslation.cs):
/// <code>
/// translationOf.Property(e => e.Containers).Is(e =>
///     (from container in Db.JobContainers.Where(w => w.ConsolidationID == e.ConsolidationID
///                                                && !string.IsNullOrEmpty(w.ContainerNumber))
///      join packing in Db.JobPackLines.Where(w => w.ShipmentID == e.ID)
///          on container.ContainerNumber equals packing.ContainerNumber into containerPacks
///      from containerPacking in containerPacks.DefaultIfEmpty()
///      where e.ShipmentType == "BRK" || containerPacking != null
///      select container).Distinct()
///     .Union(from container in Db.JobContainers.Where(w => w.ShipmentID == e.ID
///                && (w.Source == "eDC" || w.Source == "UI"))
///            select container));
/// </code>
/// </summary>
public static class ShipmentSearchMap
{
    /// <summary>
    /// Shipment.Containers as a SQL fragment correlated to the shipment alias <paramref name="s"/>.
    /// Written once, reused for filtering (EXISTS), sorting (COUNT) and loading (CROSS APPLY).
    ///
    /// Note: EF Core translates the C# `w.ConsolidationID == e.ConsolidationID` with C# null semantics
    /// (`= OR both IS NULL`). Hand-written SQL uses SQL semantics (NULL never equals NULL). That is the
    /// intended behaviour here, but it is exactly the kind of silent difference a rewrite must check.
    /// </summary>
    public static string ContainersSql(string s) => $"""
        SELECT jc.ID, jc.ContainerNumber, jc.ContainerType, jc.ShipmentID, jc.ConsolidationID, jc.Source
        FROM dbo.JobContainer jc
        WHERE jc.ConsolidationID = {s}.ConsolidationID
          AND jc.ContainerNumber IS NOT NULL AND jc.ContainerNumber <> ''
          AND ({s}.ShipmentType = 'BRK'
               OR EXISTS (SELECT 1 FROM dbo.JobPackLines pl
                          WHERE pl.ShipmentID = {s}.ID AND pl.ContainerNumber = jc.ContainerNumber))
        UNION
        SELECT jc.ID, jc.ContainerNumber, jc.ContainerType, jc.ShipmentID, jc.ConsolidationID, jc.Source
        FROM dbo.JobContainer jc
        WHERE jc.ShipmentID = {s}.ID AND jc.Source IN ('eDC', 'UI')
        """;

    public static readonly SearchMap Container = new SearchMap("JobContainer")
        .Scalar("ContainerNumber", a => $"{a}.ContainerNumber")
        .Scalar("ContainerType", a => $"{a}.ContainerType")
        .Scalar("Source", a => $"{a}.Source");

    public static readonly SearchMap Shipment = new SearchMap("Shipment")
        .Scalar("ShipmentNumber", a => $"{a}.UniqueConsignRef")
        .Scalar("HouseBill", a => $"{a}.HouseBill")
        .Scalar("ShipmentType", a => $"{a}.ShipmentType")
        .Scalar("OriginCode", a => $"{a}.OriginCode")
        .Scalar("DestinationCode", a => $"{a}.DestinationCode")
        // Computed scalar built on top of the computed collection: sortable/filterable like any column.
        .Scalar("ContainerCount", a => $"(SELECT COUNT(*) FROM ({ContainersSql(a)}) AS cc)")
        .Collection("Containers", ContainersSql, Container)
        .Reference("Consolidation", s => $"SELECT k.ID, k.ConsolNumber, k.TransportMode FROM dbo.JobConsol k WHERE k.ID = {s}.ConsolidationID", () => Consolidation);

    public static readonly SearchMap Consolidation = new SearchMap("JobConsol")
        .Scalar("ConsolNumber", a => $"{a}.ConsolNumber")
        .Scalar("TransportMode", a => $"{a}.TransportMode")
        // Navigation back to the shipments on the consol: Consolidation.Shipments.Count() > 1
        .Collection("Shipments", k => $"SELECT s2.* FROM dbo.JobShipment s2 WHERE s2.ConsolidationID = {k}.ID", Shipment);
}
