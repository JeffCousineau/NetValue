using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NetValue.Data;

internal static class HouseholdSecurityChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Denied(Action operation)
        {
            try { operation(); throw new Exception("Unauthorized operation was allowed."); }
            catch (UnauthorizedAccessException) { }
        }
        static ClaimsPrincipal Principal(Guid tenant, Guid subject, bool expired = false) => new(new ClaimsIdentity([
            new("tid", tenant.ToString()), new("oid", subject.ToString()), new("name", "Test user"),
            new("netvalue:expires", DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1).ToUnixTimeSeconds().ToString())], "Test"));
        var tenant = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        var strangerId = Guid.NewGuid();
        var owner = Principal(tenant, ownerId);
        var member = Principal(tenant, memberId);
        var stranger = Principal(tenant, strangerId);
        var root = Path.Combine(Directory.GetCurrentDirectory(), "obj", "NetValue-security-check-" + Guid.NewGuid().ToString("N"));
        var environment = new StoreEnvironment(root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Authentication:TenantId"] = tenant.ToString(), ["Households:BootstrapOwnerObjectId"] = ownerId.ToString()
        }).Build();
        var legacy = new Profile { Name = "Legacy", Accounts = [new Account { Name = "Bank", Kind = AccountKind.Cash }] };
        legacy.Months["2026-01"] = new() { [legacy.Accounts[0].Id] = 123.45m };
        new PortfolioStore(environment).Save([legacy]);
        var legacyPath = Path.Combine(root, "App_Data", "portfolio.json");
        var originalBytes = File.ReadAllBytes(legacyPath);
        var repository = new HouseholdRepository(environment, configuration);
        Denied(() => repository.Open(new ClaimsPrincipal()));
        Denied(() => repository.Open(stranger));
        Check(!File.Exists(Path.Combine(root, "App_Data", "households.json")), "A stranger must not initialize or claim the legacy household.");
        var access = repository.Open(owner);
        Check(access.Role == HouseholdRole.Owner && access.Profiles.Single().Months["2026-01"][legacy.Accounts[0].Id] == 123.45m, "Configured owner must migrate financial data exactly.");
        Check(File.ReadAllBytes(legacyPath).SequenceEqual(originalBytes), "Legacy data must remain untouched.");
        Denied(() => repository.Open(Principal(Guid.NewGuid(), ownerId)));
        Denied(() => repository.Open(Principal(tenant, ownerId, expired: true)));
        Denied(() => repository.Open(stranger));
        repository.SetMember(owner, access.Id, memberId, "Member", HouseholdRole.Member);
        Check(repository.Open(member).Role == HouseholdRole.Member, "Granted member must be able to view the household.");
        Denied(() => repository.Save(member, access.Id, access.Revision, [], restore: true));
        Denied(() => repository.SetMember(member, access.Id, strangerId, "Stranger", HouseholdRole.Owner));
        Denied(() => repository.Members(member, access.Id));
        try { repository.SetMember(owner, access.Id, ownerId, "Owner", null); throw new Exception("Last owner removal was allowed."); }
        catch (InvalidOperationException) { }
        var updated = repository.Open(member);
        var revision = repository.Save(member, access.Id, updated.Revision, updated.Profiles);
        Check(revision == updated.Revision + 1, "Member financial edits must be saved with a new revision.");
        try { repository.Save(owner, access.Id, updated.Revision, []); throw new Exception("Stale save was allowed."); }
        catch (InvalidOperationException) { }

        var storage = new RelationalHouseholdStorage(environment, configuration);
        var database = storage.Read();
        var outsider = new ApplicationUser { TenantId = tenant, ObjectId = strangerId, Name = "Other household owner" };
        var other = new Household { Name = "Other household", Profiles = [new Profile { Name = "Other private profile" }], Members = [new() { UserId = outsider.Id, Role = HouseholdRole.Owner }] };
        database.Users.Add(outsider); database.Households.Add(other);
        storage.Write(database);
        Denied(() => repository.Open(owner, other.Id));
        Denied(() => repository.Save(owner, other.Id, 0, []));
        Check(repository.Open(stranger).Profiles.Single().Name == "Other private profile", "Another household must retain its own data.");
        var current = repository.Open(owner);
        repository.Save(owner, current.Id, current.Revision, [], restore: true);
        Check(repository.Open(owner).Profiles.Count == 0 && repository.Open(stranger).Profiles.Single().Name == "Other private profile", "Restore must replace only the authorized household.");
        Check(repository.Members(owner, current.Id).Count == 2, "Financial restores must preserve membership.");
        Check(Directory.GetFiles(Path.Combine(root, "App_Data", "backups")).Length == 1, "Restore must create a household recovery backup.");
        repository.SetMember(owner, current.Id, memberId, "Member", null);
        Denied(() => repository.Open(member));
        Denied(() => repository.Save(member, current.Id, revision, []));
        Console.WriteLine("All household authorization, migration, concurrency, and recovery checks passed.");
    }
}
