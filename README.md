# Dapper search demo: `Shipment.Containers`

A proof of concept showing how eDC's EF translation `Shipment.Containers`
(`eDC.Infrastructure/DataAccess/Translations/Entity/ShipmentTranslation.cs`) can be searched,
sorted and loaded with Dapper, using dynamic filters like the grid and prefilters send.

## Run

```bash
export DEMO_SQL_CONNECTION="Server=127.0.0.1,14339;User Id=sa;Password=<sa password>;TrustServerCertificate=True"
dotnet run
```

The demo drops and recreates a database called `DapperSearchDemo` on that server, seeds 6 shipments,
and runs 9 scenarios that each check their expected results.

## Files

| File | Role | EF Core equivalent |
|---|---|---|
| `Search/SearchRequest.cs` | Filter tree, sort and paging sent by the UI | Kendo `DataSourceRequest` / prefilter criteria |
| `Search/SearchMap.cs` | Whitelist mapping logical field names to SQL | `TranslationMap` + entity mapping |
| `Search/ShipmentSearchMap.cs` | `Containers` written once as a correlated SQL fragment | `ShipmentTranslation.Register` |
| `Search/SqlFilterBuilder.cs` | Turns the filter tree into parameterised SQL (EXISTS for collections) | EF LINQ-to-SQL translator + `TranslatingVisitor` |
| `Search/CriteriaTranslator.cs` | Text criteria like `Containers.Where(ContainerType == "40HC").Any()` turned into SQL (a subset of Dynamic LINQ) | `eDC.Core/Expression/Dynamic.cs` + `Where(criteria, translator, db)` |
| `Search/FilterJson.cs` | The same filters stored as a JSON tree, no parser needed | Kendo filter descriptors / saved prefilters |
| `Data/ShipmentRepository.cs` | Count + page in one round trip, Containers via `CROSS APPLY`, division filter always applied | `WithTranslations` + `Include` + Z.EF Plus `Filter<>` |

## What this does not cover (it would be needed for a real rewrite)

- The other ~744 translations, plus per-tenant custom fields and dynamic-LINQ criteria.
- Running the same logic in memory (eDC's client translation map / `Evaluate`).
- Writes, change tracking, transactions and concurrency.
- Compile-time safety: a renamed column only fails at runtime, so every map entry needs an integration test.
- Null semantics: EF translates C# `a == b` on nullable columns as `a = b OR (a IS NULL AND b IS NULL)`;
  the hand-written SQL here uses plain `=`. Behaviour like this has to be checked translation by translation.
