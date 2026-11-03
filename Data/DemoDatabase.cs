using Dapper;
using Microsoft.Data.SqlClient;

namespace DapperSearchDemo.Data;

/// <summary>Creates a throw-away DapperSearchDemo database with a tiny slice of the eDC schema.</summary>
public static class DemoDatabase
{
    public const string Name = "DapperSearchDemo";

    public static string Recreate(string serverConnectionString)
    {
        var master = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" };
        using (var cn = new SqlConnection(master.ConnectionString))
        {
            cn.Execute($"""
                IF DB_ID('{Name}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{Name}];
                END
                CREATE DATABASE [{Name}];
                """);
        }

        var demo = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = Name }.ConnectionString;
        using var db = new SqlConnection(demo);
        db.Execute("""
            CREATE TABLE dbo.JobShipment (
                ID uniqueidentifier NOT NULL PRIMARY KEY,
                UniqueConsignRef nvarchar(20) NOT NULL,
                HouseBill nvarchar(30) NULL,
                ShipmentType nvarchar(3) NULL,
                OriginCode nvarchar(5) NULL,
                DestinationCode nvarchar(5) NULL,
                ConsolidationID uniqueidentifier NULL,
                CustomerDivision nvarchar(20) NULL);

            CREATE TABLE dbo.JobContainer (
                ID uniqueidentifier NOT NULL PRIMARY KEY,
                ContainerNumber nvarchar(20) NULL,
                ContainerType nvarchar(10) NULL,
                ShipmentID uniqueidentifier NULL,
                ConsolidationID uniqueidentifier NULL,
                Source nvarchar(10) NULL);

            CREATE TABLE dbo.JobPackLines (
                ID uniqueidentifier NOT NULL PRIMARY KEY,
                ShipmentID uniqueidentifier NULL,
                ContainerNumber nvarchar(20) NULL);

            CREATE TABLE dbo.JobConsol (
                ID uniqueidentifier NOT NULL PRIMARY KEY,
                ConsolNumber nvarchar(20) NOT NULL,
                TransportMode nvarchar(3) NULL);

            CREATE INDEX IX_JobContainer_Consol ON dbo.JobContainer (ConsolidationID, ContainerNumber);
            CREATE INDEX IX_JobContainer_Shipment ON dbo.JobContainer (ShipmentID, Source);
            CREATE INDEX IX_JobPackLines_Shipment ON dbo.JobPackLines (ShipmentID, ContainerNumber);
            """);

        Seed(db);
        return demo;
    }

    /// <summary>
    /// Consol K1 holds MSCU1234567 (40HC), TGHU7654321 (20GP) and a container with no number.
    ///   S0001 FCL on K1, packed in MSCU                  -> Containers: MSCU
    ///   S0002 FCL on K1, packed in TGHU                  -> Containers: TGHU
    ///   S0003 BRK on K1, no packlines (BRK rule)         -> Containers: MSCU, TGHU
    ///   S0004 no consol, own UI container + a CW one     -> Containers: CAIU (CW source excluded)
    ///   S0005 division SOUTH, consol K2 with MSCU0000001 -> hidden from NORDIC users
    ///   S0006 no containers at all
    /// </summary>
    private static void Seed(SqlConnection db)
    {
        var k1 = Guid.NewGuid();
        var k2 = Guid.NewGuid();
        Guid S(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");

        db.Execute("INSERT dbo.JobConsol VALUES (@ID, @No, @Mode)", new[]
        {
            new { ID = k1, No = "C0001", Mode = "SEA" },
            new { ID = k2, No = "C0002", Mode = "AIR" },
        });

        db.Execute("INSERT dbo.JobShipment VALUES (@ID, @Ref, @HB, @Type, @O, @D, @Consol, @Div)", new[]
        {
            new { ID = S(1), Ref = "S0001", HB = "HB-1", Type = "FCL", O = "DKAAR", D = "CNSHA", Consol = (Guid?)k1, Div = "NORDIC" },
            new { ID = S(2), Ref = "S0002", HB = "HB-2", Type = "FCL", O = "DKAAR", D = "CNSHA", Consol = (Guid?)k1, Div = "NORDIC" },
            new { ID = S(3), Ref = "S0003", HB = "HB-3", Type = "BRK", O = "DKCPH", D = "USNYC", Consol = (Guid?)k1, Div = "NORDIC" },
            new { ID = S(4), Ref = "S0004", HB = "HB-4", Type = "LCL", O = "SEGOT", D = "USNYC", Consol = (Guid?)null, Div = "NORDIC" },
            new { ID = S(5), Ref = "S0005", HB = "HB-5", Type = "FCL", O = "ESVLC", D = "CNSHA", Consol = (Guid?)k2, Div = "SOUTH" },
            new { ID = S(6), Ref = "S0006", HB = "HB-6", Type = "LCL", O = "DKAAR", D = "DEHAM", Consol = (Guid?)null, Div = "NORDIC" },
        });

        db.Execute("INSERT dbo.JobContainer VALUES (NEWID(), @No, @Type, @Ship, @Consol, @Src)", new[]
        {
            new { No = "MSCU1234567", Type = "40HC", Ship = (Guid?)null, Consol = (Guid?)k1, Src = "CW" },
            new { No = "TGHU7654321", Type = "20GP", Ship = (Guid?)null, Consol = (Guid?)k1, Src = "CW" },
            new { No = "",            Type = "20GP", Ship = (Guid?)null, Consol = (Guid?)k1, Src = "CW" },
            new { No = "CAIU5555555", Type = "40GP", Ship = (Guid?)S(4), Consol = (Guid?)null, Src = "UI" },
            new { No = "OOLU9999999", Type = "40GP", Ship = (Guid?)S(4), Consol = (Guid?)null, Src = "CW" },
            new { No = "MSCU0000001", Type = "40HC", Ship = (Guid?)null, Consol = (Guid?)k2, Src = "CW" },
        });

        db.Execute("INSERT dbo.JobPackLines VALUES (NEWID(), @Ship, @No)", new[]
        {
            new { Ship = S(1), No = "MSCU1234567" },
            new { Ship = S(2), No = "TGHU7654321" },
            new { Ship = S(5), No = "MSCU0000001" },
        });
    }
}
