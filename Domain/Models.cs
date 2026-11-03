namespace DapperSearchDemo.Domain;

// Trimmed-down versions of eDC.Domain.Model.Shipment / JobContainer.

public class Shipment
{
    public Guid ID { get; set; }
    public string UniqueConsignRef { get; set; } = "";
    public string? HouseBill { get; set; }
    public string? ShipmentType { get; set; }
    public string? OriginCode { get; set; }
    public string? DestinationCode { get; set; }
    public Guid? ConsolidationID { get; set; }
    public string? CustomerDivision { get; set; }

    /// <summary>Computed collection (see ShipmentSearchMap.ContainersSql). Filled only when requested.</summary>
    public List<JobContainer> Containers { get; set; } = new();
}

public class JobContainer
{
    public Guid ID { get; set; }
    public string? ContainerNumber { get; set; }
    public string? ContainerType { get; set; }
    public Guid? ShipmentID { get; set; }
    public Guid? ConsolidationID { get; set; }
    public string? Source { get; set; }
}

/// <summary>Row shape used when loading Containers for a page of shipments.</summary>
public sealed class ShipmentContainerRow : JobContainer
{
    public Guid OwnerShipmentID { get; set; }
}

/// <summary>Who is searching: replaces the Z.EF Plus ctx.Filter&lt;Shipment&gt;(...) division filter in DbFactory.</summary>
public sealed record UserContext(IReadOnlyList<string> ActiveCustomerDivisions, bool IncludeUnassignedDivision);
