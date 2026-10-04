using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using NetValue.Data;

internal static class SelfServiceChecks
{
    public static void Run()
    {
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Denied(Action action)
        {
            try { action(); throw new Exception("Unauthorized self-service operation was allowed."); }
            catch (UnauthorizedAccessException) { }
        }
        static ClaimsPrincipal Principal(Guid tenant, Guid subject, bool expired = false) => new(new ClaimsIdentity([
            new("tid", tenant.ToString()), new("oid", subject.ToString()), new("name", "New user"),
            new("netvalue:expires", DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1).ToUnixTimeSeconds().ToString())], "Test"));
        var root = Path.Combine(Directory.GetCurrentDirectory(), "obj", "NetValue-signup-check-" + Guid.NewGuid().ToString("N"));
        var tenant = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Authentication:TenantId"] = tenant.ToString(), ["Authentication:SelfServiceEnabled"] = "true",
            ["Households:BootstrapOwnerObjectId"] = ownerId.ToString()
        }).Build();
        var environment = new StoreEnvironment(root);
        var repository = new HouseholdRepository(environment, config);
        new PortfolioStore(environment).Save([new Profile { Name = "Private legacy data" }]);
        var sharedObject = Guid.NewGuid();
        var personal = Principal(Guid.Parse("9188040d-6c67-4c5b-b112-36a304b66dad"), sharedObject);
        var work = Principal(Guid.NewGuid(), sharedObject);
        Denied(() => repository.Open(personal));
        try { repository.CreateHousehold(personal, "New household"); throw new Exception("Registration bypassed pending migration."); }
        catch (InvalidOperationException) { }
        var owner = Principal(tenant, ownerId);
        var original = repository.Open(owner);
        Check(repository.CanCreateHousehold(personal), "Personal Microsoft accounts must be eligible for registration.");
        try { repository.CreateHousehold(personal, " "); throw new Exception("Blank household name accepted."); }
        catch (InvalidDataException) { }
        var created = repository.CreateHousehold(personal, " Personal household ");
        Check(created.Name == "Personal household" && created.Role == HouseholdRole.Owner && created.Profiles.Count == 0, "Registration must create an empty owned household.");
        Parallel.For(0, 4, _ => Check(repository.CreateHousehold(personal, "Duplicate").Id == created.Id, "Repeated registration created a duplicate household."));
        var other = repository.CreateHousehold(work, "Work household");
        Check(other.Id != created.Id, "Identical Object IDs from different tenants must remain separate users.");
        Denied(() => repository.Open(personal, original.Id));
        Denied(() => repository.Save(personal, original.Id, original.Revision, []));
        Denied(() => repository.Save(personal, original.Id, original.Revision, [], restore: true));
        Denied(() => repository.Members(personal, original.Id));
        Denied(() => repository.SetMember(personal, original.Id, sharedObject, "Intruder", HouseholdRole.Owner));
        Check(repository.Open(owner).Profiles.Single().Name == "Private legacy data", "Sign-up must preserve the existing household data.");
        Denied(() => repository.CreateHousehold(new ClaimsPrincipal(), "Anonymous"));
        Denied(() => repository.CreateHousehold(Principal(Guid.NewGuid(), Guid.NewGuid(), true), "Expired"));
        Denied(() => repository.CreateHousehold(Principal(Guid.Empty, Guid.NewGuid()), "Invalid tenant"));
        var memberId = Guid.NewGuid();
        var member = Principal(tenant, memberId);
        repository.SetMember(owner, original.Id, memberId, "Existing member", HouseholdRole.Member);
        Check(!repository.CanCreateHousehold(member) && repository.CreateHousehold(member, "Unneeded").Id == original.Id, "Existing members must retain their household.");
        repository.SetMember(owner, original.Id, memberId, "Existing member", null);
        Check(!repository.CanCreateHousehold(member), "Revoked users must not be automatically registered again.");
        Denied(() => repository.CreateHousehold(member, "Re-register"));
        Check(new RelationalHouseholdStorage(environment, config).Read().Households.Count == 3, "Registration must not create duplicate households.");
        config["Authentication:SelfServiceEnabled"] = "false";
        Denied(() => repository.Open(personal));
        Denied(() => repository.CreateHousehold(Principal(tenant, Guid.NewGuid()), "Disabled"));
        Console.WriteLine("All Microsoft self-service registration and household isolation checks passed.");
    }
}
