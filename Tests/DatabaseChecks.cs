using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using NetValue.Data;

internal static class DatabaseChecks
{
    public static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } catch (InvalidOperationException) { return; } throw new Exception("Invalid database operation succeeded."); }
        var root = Path.Combine(Directory.GetCurrentDirectory(), "obj", "database-check-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid(); var subject = Guid.NewGuid();
        var environment = new StoreEnvironment(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Authentication:TenantId"] = tenant.ToString(), ["Households:BootstrapOwnerObjectId"] = subject.ToString()
        }).Build();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new("tid", tenant.ToString()), new("oid", subject.ToString()), new("netvalue:expires", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString())], "Test"));
        var user = new ApplicationUser { TenantId = tenant, ObjectId = subject, Name = "Owner" };
        var profile = new Profile { Name = "Imported profile", Accounts = [new() { Name = "Zero", Institution = "Desjardins" }, new() { Name = "Missing" }, new() { Name = "Precise" }] };
        profile.Months["2026-01"] = new() { [profile.Accounts[0].Id] = 0.00m, [profile.Accounts[2].Id] = 123456789.12m };
        profile.Months["2026-02"] = []; // Preserve empty saved snapshots too.
        var household = new Household { Name = "Imported household", Revision = 7, Profiles = [profile], Members = [new() { UserId = user.Id, Role = HouseholdRole.Owner }] };
        var legacy = new HouseholdDatabase { Users = [user], Households = [household] };
        Directory.CreateDirectory(Path.Combine(root, "App_Data"));
        var jsonPath = Path.Combine(root, "App_Data", "households.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(legacy));
        var original = File.ReadAllBytes(jsonPath);
        var repository = new HouseholdRepository(environment, config);
        var access = repository.Open(principal);
        Check(access.Id == household.Id && access.Revision == 7 && access.Role == HouseholdRole.Owner, "Household import must preserve IDs, revision, and roles.");
        Check(File.ReadAllBytes(jsonPath).SequenceEqual(original), "Existing household JSON must remain untouched.");
        var restoredProfile = access.Profiles.Single();
        Check(restoredProfile.Accounts.Select(a => a.Id).SequenceEqual(profile.Accounts.Select(a => a.Id)) && restoredProfile.Accounts[0].Institution == "Desjardins", "Account order and institutions must survive database storage.");
        Check(restoredProfile.Months["2026-01"].Count == 2 && restoredProfile.Months["2026-01"][profile.Accounts[0].Id] == 0m && restoredProfile.Months["2026-01"][profile.Accounts[2].Id] == 123456789.12m && restoredProfile.Months["2026-02"].Count == 0, "Decimal amounts, zero, missing entries, and empty snapshots must survive migration.");
        var newRepository = new HouseholdRepository(environment, config);
        Check(newRepository.Open(principal).Profiles.Single().Months["2026-01"].Count == 2, "Data must persist across repository restarts.");
        repository.Save(principal, access.Id, access.Revision, []);
        Check(newRepository.Open(principal).Profiles.Count == 0, "Legacy JSON must not be re-imported after database edits.");

        var storage = new RelationalHouseholdStorage(environment, config);
        var first = storage.Read(); var stale = storage.Read();
        first.Households[0].Name = "Updated"; storage.Write(first);
        stale.Households[0].Name = "Lost update"; Reject(() => storage.Write(stale));
        Check(storage.Read().Households[0].Name == "Updated", "Independent storage instances must reject stale snapshot writes atomically.");
        var invalid = storage.Read(); invalid.Households[0].Profiles = [profile]; profile.Months["2026-01"][profile.Accounts[0].Id] = 0.001m;
        Reject(() => storage.Write(invalid));
        Check(storage.Read().Households[0].Profiles.Count == 0, "Rejected money precision must leave existing data intact.");
        profile.Months["2026-01"][profile.Accounts[0].Id] = 0m;
        invalid.Households[0].Profiles = [profile]; storage.Write(invalid);

        var backup = Path.Combine(root, "operator.json"); DatabaseTransfer.Export(storage, backup);
        var destination = new RelationalHouseholdStorage(new StoreEnvironment(Path.Combine(root, "destination")), config);
        DatabaseTransfer.Import(destination, backup);
        var imported = destination.Read();
        Check(imported.Users.Single().ObjectId == subject && imported.Households.Single().Id == household.Id && imported.Households.Single().Profiles.Single().Months["2026-01"].Count == 2, "Operator transfer must preserve ownership and complete financial data.");
        Reject(() => DatabaseTransfer.Import(destination, backup));
        Check(destination.Read().Households.Single().Id == household.Id, "Operator import must refuse an occupied destination.");
        var externalPath = Path.Combine(root, "external");
        var externalConfig = new ConfigurationBuilder().AddConfiguration(config).AddInMemoryCollection(new Dictionary<string, string?> { ["Database:DataDirectory"] = externalPath }).Build();
        var external = new RelationalHouseholdStorage(environment, externalConfig); external.Migrate();
        Check(File.Exists(Path.Combine(externalPath, "netvalue.db")), "External data directory must separate database from application files.");
        Reject(() => new RelationalHouseholdStorage(new StoreEnvironment(root) { EnvironmentName = "Production" }, config));

        using var sql = new SqlServerNetValueContext(new DbContextOptionsBuilder<SqlServerNetValueContext>().UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True").Options);
        var script = sql.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Check(script.Contains("decimal(18,2)") && script.Contains("CREATE TABLE [Members]") && script.Contains("CREATE TABLE [Balances]") && script.Contains("__EFMigrationsHistory"), "SQL Server migration must generate a versioned decimal schema without connecting to a server.");
        Check(!sql.Database.HasPendingModelChanges(), "SQL Server migration snapshot must match the model.");
        using var sqlite = new SqliteNetValueContext(new DbContextOptionsBuilder<SqliteNetValueContext>().UseSqlite("Data Source=:memory:").Options);
        Check(!sqlite.Database.HasPendingModelChanges(), "SQLite migration snapshot must match the model.");
        Console.WriteLine("All relational persistence, provider schema, precision, concurrency, JSON migration, and operator transfer checks passed.");
    }
}
