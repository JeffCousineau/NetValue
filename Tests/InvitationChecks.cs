using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using NetValue.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

internal static class InvitationChecks
{
    public static void Run()
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Reject<T>(Action action) where T : Exception
        {
            try { action(); throw new Exception("Invitation operation unexpectedly succeeded."); }
            catch (T) { }
        }
        static ClaimsPrincipal User(Guid tenant, Guid oid) => new(new ClaimsIdentity([
            new("tid", tenant.ToString()), new("oid", oid.ToString()), new("name", "Invitation test"),
            new("netvalue:expires", DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString())], "Test"));
        var environment = new StoreEnvironment(Path.Combine(Directory.GetCurrentDirectory(), "obj", "invitations-" + Guid.NewGuid().ToString("N")));
        var tenant = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Authentication:TenantId"] = tenant.ToString(), ["Authentication:SelfServiceEnabled"] = "true",
            ["Households:BootstrapOwnerObjectId"] = ownerId.ToString()
        }).Build();
        var dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);
        var existingHouseholdId = Guid.NewGuid();
        var existingOwner = new ApplicationUser { TenantId = tenant, ObjectId = ownerId, Name = "Existing owner" };
        using (var oldDatabase = new SqliteNetValueContext(new DbContextOptionsBuilder<SqliteNetValueContext>().UseSqlite("Data Source=" + Path.Combine(dataDirectory, "netvalue.db")).Options))
        {
            oldDatabase.GetService<IMigrator>().Migrate("20261004014147_InitialSqlite");
            oldDatabase.Storage.Add(new() { Id = 1, Revision = 1 });
            oldDatabase.Users.Add(existingOwner);
            oldDatabase.Households.Add(new() { Id = existingHouseholdId, Name = "Existing household" });
            oldDatabase.Members.Add(new() { HouseholdId = existingHouseholdId, UserId = existingOwner.Id, Role = HouseholdRole.Owner });
            oldDatabase.Profiles.Add(new() { HouseholdId = existingHouseholdId, Id = Guid.NewGuid(), Name = "Before invitations" });
            oldDatabase.SaveChanges();
        }
        var repo = new HouseholdRepository(environment, config);
        var owner = User(tenant, ownerId);
        var household = repo.Open(owner);
        Check(household.Id == existingHouseholdId && household.Profiles.Single().Name == "Before invitations", "Upgrading the existing schema must preserve household ownership and financial records.");
        var newcomer = User(Guid.NewGuid(), Guid.NewGuid());
        var token = repo.CreateInvitation(owner, household.Id);
        var storage = new RelationalHouseholdStorage(environment, config);
        Check(storage.Read().Invitations.Single().TokenHash != token, "Only a hash of the invitation secret must be stored.");
        Reject<UnauthorizedAccessException>(() => repo.PreviewInvitation(new ClaimsPrincipal(), token));
        Reject<UnauthorizedAccessException>(() => repo.CreateInvitation(newcomer, household.Id));
        Check(repo.PreviewInvitation(newcomer, token).HouseholdName == household.Name, "A signed-in recipient must be able to preview the invitation.");
        Reject<UnauthorizedAccessException>(() => repo.Open(newcomer, household.Id));
        var joined = new HouseholdRepository(environment, config).AcceptInvitation(newcomer, token);
        Check(joined.Id == household.Id && joined.Role == HouseholdRole.Member && !repo.CanCreateHousehold(newcomer), "A new recipient must join without creating an unrelated household.");
        Reject<InvalidDataException>(() => repo.AcceptInvitation(newcomer, token));
        Reject<UnauthorizedAccessException>(() => repo.CreateInvitation(newcomer, household.Id));
        Reject<UnauthorizedAccessException>(() => repo.RevokeInvitation(newcomer, household.Id, Guid.NewGuid()));
        Reject<UnauthorizedAccessException>(() => repo.Save(newcomer, household.Id, joined.Revision, [], restore: true));
        var sharedOid = Guid.NewGuid();
        var existing = User(Guid.NewGuid(), sharedOid);
        var own = repo.CreateHousehold(existing, "Recipient's household");
        var token2 = repo.CreateInvitation(owner, household.Id);
        repo.AcceptInvitation(existing, token2);
        Check(repo.Households(existing).Count == 2 && repo.Open(existing, own.Id).Id == own.Id && repo.Open(existing, household.Id).Role == HouseholdRole.Member, "Existing households must survive joining, and both must remain selectable.");
        var sameOid = User(Guid.NewGuid(), sharedOid);
        repo.AcceptInvitation(sameOid, repo.CreateInvitation(owner, household.Id));
        var existingUserId = repo.UserId(existing);
        repo.RemoveMember(owner, household.Id, existingUserId);
        Reject<UnauthorizedAccessException>(() => repo.Open(existing, household.Id));
        Check(repo.Open(sameOid, household.Id).Role == HouseholdRole.Member && repo.Open(existing, own.Id).Role == HouseholdRole.Owner, "Removal must target the internal user, not a colliding Object ID in another tenant.");
        Reject<InvalidOperationException>(() => repo.RemoveMember(owner, household.Id, repo.UserId(owner)));
        Reject<InvalidOperationException>(() => repo.SetMemberRole(owner, household.Id, repo.UserId(owner), HouseholdRole.Member));
        Reject<UnauthorizedAccessException>(() => repo.SetMemberRole(newcomer, household.Id, repo.UserId(newcomer), HouseholdRole.Owner));
        repo.SetMemberRole(owner, household.Id, repo.UserId(sameOid), HouseholdRole.Owner);
        Check(repo.Open(sameOid, household.Id).Role == HouseholdRole.Owner, "Owners must be able to promote members across directories.");
        repo.SetMemberRole(owner, household.Id, repo.UserId(sameOid), HouseholdRole.Member);
        var revoked = repo.CreateInvitation(owner, household.Id);
        var invitationId = repo.Invitations(owner, household.Id).Single().Id;
        repo.RevokeInvitation(owner, household.Id, invitationId);
        Reject<InvalidDataException>(() => repo.AcceptInvitation(existing, revoked));
        var expired = repo.CreateInvitation(owner, household.Id);
        var db = storage.Read();
        db.Invitations.Single().ExpiresUtc = DateTime.UtcNow.AddSeconds(-1);
        storage.Write(db);
        Reject<InvalidDataException>(() => repo.AcceptInvitation(existing, expired));
        Reject<InvalidDataException>(() => repo.AcceptInvitation(existing, new string('Z', 64)));
        var raceToken = repo.CreateInvitation(owner, household.Id);
        var successes = 0;
        Parallel.For(0, 4, _ => {
            try { repo.AcceptInvitation(existing, raceToken); Interlocked.Increment(ref successes); }
            catch (InvalidDataException) { }
        });
        Check(successes == 1, "A link must be accepted once even with simultaneous submissions.");
        var secondOwnerId = Guid.NewGuid();
        repo.SetMember(owner, household.Id, secondOwnerId, "Second owner", HouseholdRole.Owner);
        var staleOwnerToken = repo.CreateInvitation(owner, household.Id);
        repo.RemoveMember(User(tenant, secondOwnerId), household.Id, repo.UserId(owner));
        Reject<InvalidDataException>(() => repo.AcceptInvitation(existing, staleOwnerToken));
        Console.WriteLine("All invitation expiry, revocation, identity, isolation, persistence, and single-use checks passed.");
    }
}
