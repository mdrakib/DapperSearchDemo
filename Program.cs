using DapperSearchDemo.Data;
using DapperSearchDemo.Domain;
using DapperSearchDemo.Search;
using Microsoft.Data.SqlClient;
using static DapperSearchDemo.Search.F;

var server = Environment.GetEnvironmentVariable("DEMO_SQL_CONNECTION")
    ?? throw new InvalidOperationException(
        "Set DEMO_SQL_CONNECTION, e.g. \"Server=127.0.0.1,14339;User Id=sa;Password=...;TrustServerCertificate=True\"");

var connectionString = DemoDatabase.Recreate(server);
using var db = new SqlConnection(connectionString);
var repo = new ShipmentRepository(db);

var nordic = new UserContext(["NORDIC"], IncludeUnassignedDivision: false);
var south = new UserContext(["SOUTH"], IncludeUnassignedDivision: false);
var failures = 0;

Run("1. Containers.ContainerNumber contains 'MSCU' (S0005 matches but is another division)",
    new SearchRequest { Filter = Where("Containers.ContainerNumber", Op.Contains, "MSCU") },
    nordic, ["S0001", "S0003"], printSql: true);

Run("2. Same search as a SOUTH user: the division filter is always applied",
    new SearchRequest { Filter = Where("Containers.ContainerNumber", Op.Contains, "MSCU") },
    south, ["S0005"]);

Run("3a. Two separate container conditions: they may match DIFFERENT containers",
    new SearchRequest
    {
        Filter = And(
            Where("Containers.ContainerNumber", Op.StartsWith, "MSCU"),
            Where("Containers.ContainerType", Op.Eq, "20GP")),
    },
    nordic, ["S0003"]);

Run("3b. Any(container => number starts 'MSCU' AND type = '20GP'): must be the SAME container",
    new SearchRequest
    {
        Filter = Where("Containers", Op.Any, And(
            Where("ContainerNumber", Op.StartsWith, "MSCU"),
            Where("ContainerType", Op.Eq, "20GP"))),
    },
    nordic, [], printSql: true);

Run("4. Shipments with no containers",
    new SearchRequest { Filter = Where("Containers", Op.None) },
    nordic, ["S0006"]);

Run("5. OR across a shipment column and a container field, sorted by computed ContainerCount",
    new SearchRequest
    {
        Filter = Or(
            Where("ShipmentNumber", Op.Eq, "S0004"),
            Where("Containers.ContainerNumber", Op.Contains, "TGHU")),
        Sort = [new SortSpec("ContainerCount", Descending: true), new SortSpec("ShipmentNumber")],
    },
    nordic, ["S0003", "S0002", "S0004"], includeContainers: true, printSql: true);

Run("6. Container number IN list ('OOLU...' is CW-sourced, so not part of S0004.Containers)",
    new SearchRequest { Filter = Where("Containers.ContainerNumber", Op.In, new[] { "CAIU5555555", "OOLU9999999" }) },
    nordic, ["S0004"]);

Run("7. Paging: all NORDIC shipments, page 2 of size 2",
    new SearchRequest { Sort = [new SortSpec("ShipmentNumber")], Skip = 2, Take = 2 },
    nordic, ["S0003", "S0004"], expectedTotal: 5);

Run("8. SQL-injection style value is just a parameter (no match, nothing breaks)",
    new SearchRequest { Filter = Where("Containers.ContainerNumber", Op.Contains, "x'; DROP TABLE dbo.JobContainer;--") },
    nordic, []);

// 9. Unknown / malicious field names are rejected by the whitelist before any SQL is built.
try
{
    repo.Search(new SearchRequest { Filter = Where("Containers.ID; DROP TABLE x", Op.Eq, 1) }, nordic);
    Report("9. Unknown field is rejected", false, "no exception thrown");
}
catch (UnknownFieldException ex)
{
    Report("9. Unknown field is rejected", true, ex.Message);
}

Console.WriteLine("\n---- Dynamic-LINQ style criteria strings (as stored in tenant configuration) ----\n");

Criteria("10. Containers.Where(ContainerType = \"40HC\").Any()", nordic, ["S0001", "S0003"]);
Criteria("11. Containers.Any(ContainerNumber.StartsWith(\"MSCU\") && ContainerType == \"20GP\")", nordic, []);
Criteria("12. Containers.Count() >= 2 || (OriginCode == \"SEGOT\" && Containers.Any())", nordic, ["S0003", "S0004"]);
Criteria("13. Consolidation.TransportMode == \"SEA\" && !Containers.Any(ContainerType == \"40HC\")", nordic, ["S0002"], printSql: true);
Criteria("14. Containers.All(ContainerType == \"20GP\")", nordic, ["S0002", "S0006"]); // All() on empty = true, like C#
Criteria("15. Consolidation.Shipments.Count() >= 3 and ShipmentType != \"BRK\"", nordic, ["S0001", "S0002"]);
Criteria("16. ShipmentNumber == \"x\\\" || 1 == 1 --\"", nordic, []); // quote inside literal stays data
CriteriaError("17. Containers.Where(Code = \"ABC\").Any()");          // your example: Code is not whitelisted
CriteriaError("18. Containers.Any(ContainerType == \"40HC\"");          // syntax error
CriteriaError("19. Containers == \"MSCU\"");                             // type error: collection used as value

Console.WriteLine("\n---- Same criteria stored as a JSON filter tree (no parser needed) ----\n");

// Text equivalent: (Containers.Any(ContainerType == "20GP") || OriginCode in ("SEGOT","DEHAM")) && ShipmentType != "BRK"
const string storedJson = """
    { "and": [
        { "or": [
            { "field": "Containers", "op": "any",
              "where": { "field": "ContainerType", "op": "eq", "value": "20GP" } },
            { "field": "OriginCode", "op": "in", "value": ["SEGOT", "DEHAM"] }
        ]},
        { "field": "ShipmentType", "op": "neq", "value": "BRK" }
    ]}
    """;

Run("20. JSON filter: (any 20GP container OR origin in [SEGOT, DEHAM]) AND not BRK",
    new SearchRequest { Filter = FilterJson.Parse(storedJson), Sort = [new SortSpec("ShipmentNumber")] },
    nordic, ["S0002", "S0004"]);

Console.WriteLine(failures == 0 ? "\nAll scenarios passed." : $"\n{failures} scenario(s) FAILED.");
return failures == 0 ? 0 : 1;

void Run(string title, SearchRequest request, UserContext user, string[] expected,
         int? expectedTotal = null, bool includeContainers = false, bool printSql = false)
{
    var result = repo.Search(request, user, includeContainers);
    var actual = result.Items.Select(s => s.UniqueConsignRef).ToArray();
    var ok = actual.SequenceEqual(expected) && result.Total == (expectedTotal ?? expected.Length);

    Report(title, ok, $"total={result.Total} items=[{string.Join(", ", actual)}]" +
                      (ok ? "" : $"  expected=[{string.Join(", ", expected)}]"));

    if (includeContainers)
        foreach (var s in result.Items)
            Console.WriteLine($"      {s.UniqueConsignRef}: {string.Join(", ", s.Containers.Select(c => $"{c.ContainerNumber} ({c.ContainerType})"))}");

    if (printSql)
        Console.WriteLine("\n" + Indent(result.Sql.Split("\n\n")[^1]) + "\n");
}

// Title format: "<n>. <criteria>"
void Criteria(string title, UserContext user, string[] expected, bool printSql = false) =>
    Run(title, new SearchRequest { Criteria = title[(title.IndexOf(". ") + 2)..], Sort = [new SortSpec("ShipmentNumber")] },
        user, expected, printSql: printSql);

void CriteriaError(string title)
{
    try
    {
        repo.Search(new SearchRequest { Criteria = title[(title.IndexOf(". ") + 2)..] }, nordic);
        Report(title + "  -> rejected", false, "no exception thrown");
    }
    catch (CriteriaException ex)
    {
        Report(title + "  -> rejected", true, ex.Message.Replace("\n", "\n      "));
    }
}

void Report(string title, bool ok, string detail)
{
    if (!ok) failures++;
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {title}\n      {detail}");
}

static string Indent(string sql) => string.Join("\n", sql.Split('\n').Select(l => "      | " + l));
