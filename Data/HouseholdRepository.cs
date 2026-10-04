using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Security.Cryptography;

namespace NetValue.Data;

public enum HouseholdRole { Member, Owner }
public sealed class ApplicationUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ObjectId { get; set; }
    public string Name { get; set; } = "";
}
public sealed class HouseholdMembership
{
    public Guid UserId { get; set; }
    public HouseholdRole Role { get; set; }
}
public sealed class Household
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public long Revision { get; set; }
    public List<HouseholdMembership> Members { get; set; } = [];
    public List<Profile> Profiles { get; set; } = [];
}
public sealed class HouseholdDatabase
{
    [JsonIgnore] public long StorageRevision { get; set; }
    public int Version { get; set; } = 1;
    public List<ApplicationUser> Users { get; set; } = [];
    public List<Household> Households { get; set; } = [];
    public List<HouseholdInvitation> Invitations { get; set; } = [];
}
public sealed class HouseholdInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid CreatedBy { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
}
public sealed record InvitationPreview(string HouseholdName, DateTime ExpiresUtc);
public sealed record HouseholdChoice(Guid Id, string Name);
public sealed record HouseholdAccess(Guid Id, string Name, HouseholdRole Role, long Revision, List<Profile> Profiles);
public sealed record MemberInfo(Guid ObjectId, string Name, HouseholdRole Role, Guid UserId = default);

public sealed class HouseholdRepository
{
    private readonly string path;
    private readonly string root;
    private readonly Guid tenantId;
    private readonly IConfiguration configuration;
    private readonly RelationalHouseholdStorage storage;
    private readonly object gate = new();
    public HouseholdRepository(IWebHostEnvironment environment, IConfiguration configuration)
    {
        this.configuration = configuration;
        storage = new(environment, configuration);
        tenantId = Guid.Parse(configuration["Authentication:TenantId"]!);
        root = Path.GetFullPath(configuration["Database:DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data"), environment.ContentRootPath);
        path = Path.Combine(root, "households.json");
    }
    public (Guid Tenant, Guid Object) Identity(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true
            || !Guid.TryParse(principal.FindFirstValue("tid"), out var tenant) || tenant == Guid.Empty
            || (!configuration.GetValue<bool>("Authentication:SelfServiceEnabled") && tenant != tenantId)
            || !Guid.TryParse(principal.FindFirstValue("oid"), out var subject) || subject == Guid.Empty
            || !long.TryParse(principal.FindFirstValue("netvalue:expires"), out var expires) || expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new UnauthorizedAccessException("Please sign in again with an authorized Microsoft account.");
        return (tenant, subject);
    }
    public HouseholdAccess Open(ClaimsPrincipal principal, Guid? householdId = null)
    {
        var identity = Identity(principal);
        lock (gate)
        {
            var database = Read();
            if (database.Households.Count == 0 && database.StorageRevision == 0
                && identity.Tenant == tenantId && Guid.TryParse(configuration["Households:BootstrapOwnerObjectId"], out var owner) && owner == identity.Object)
            { Bootstrap(principal, identity); database = Read(); }
            return Access(principal, database, householdId);
        }
    }
    public bool CanCreateHousehold(ClaimsPrincipal principal)
    {
        var identity = Identity(principal);
        lock (gate)
            return configuration.GetValue<bool>("Authentication:SelfServiceEnabled")
                && !Read().Users.Any(u => u.TenantId == identity.Tenant && u.ObjectId == identity.Object);
    }
    public HouseholdAccess CreateHousehold(ClaimsPrincipal principal, string name)
    {
        var identity = Identity(principal);
        if (!configuration.GetValue<bool>("Authentication:SelfServiceEnabled")) throw new UnauthorizedAccessException("Self-service registration is disabled.");
        name = name.Trim();
        if (name.Length is < 1 or > 100) throw new InvalidDataException("Enter a household name between 1 and 100 characters.");
        lock (gate)
        {
            var database = Read();
            // Repeat submissions return existing access; revoked users cannot regain access by registering again.
            if (database.Users.Any(u => u.TenantId == identity.Tenant && u.ObjectId == identity.Object)) return Access(principal, database, null);
            if (database.StorageRevision == 0 && (File.Exists(path) || File.Exists(Path.Combine(root, "portfolio.json"))))
                throw new InvalidOperationException("The initial household owner must finish importing existing data before registration is available.");
            var displayName = principal.FindFirstValue("name")?.Trim();
            if (string.IsNullOrWhiteSpace(displayName)) displayName = "Household owner";
            var user = new ApplicationUser { TenantId = identity.Tenant, ObjectId = identity.Object, Name = displayName[..Math.Min(displayName.Length, 100)] };
            var household = new Household { Name = name, Members = [new() { UserId = user.Id, Role = HouseholdRole.Owner }] };
            database.Users.Add(user);
            database.Households.Add(household);
            Write(database);
            return Access(principal, database, household.Id);
        }
    }
    private HouseholdAccess Access(ClaimsPrincipal principal, HouseholdDatabase database, Guid? householdId)
    {
            var identity = Identity(principal);
            var user = database.Users.SingleOrDefault(user => user.TenantId == identity.Tenant && user.ObjectId == identity.Object);
            var household = database.Households.FirstOrDefault(h => (!householdId.HasValue || h.Id == householdId) && h.Members.Any(member => member.UserId == user?.Id));
            if (household is null || user is null) throw new UnauthorizedAccessException("Your account has not been given access to a household. Ask a household owner to add your Entra user Object ID.");
            var role = household.Members.Single(member => member.UserId == user.Id).Role;
            if (!Enum.IsDefined(role)) throw new UnauthorizedAccessException("This household membership is invalid.");
            return new(household.Id, household.Name, role, household.Revision, household.Profiles);
    }
    public long Save(ClaimsPrincipal principal, Guid householdId, long revision, List<Profile> profiles, bool restore = false)
    {
        PortfolioBackup.Validate(profiles);
        lock (gate)
        {
            var database = Read();
            var access = Access(principal, database, householdId);
            if (restore && access.Role != HouseholdRole.Owner) throw new UnauthorizedAccessException("Only household owners can restore backups.");
            if (access.Revision != revision) throw new InvalidOperationException("This household was updated in another tab. Reload the page before editing again.");
            var household = database.Households.Single(h => h.Id == householdId);
            if (restore)
            {
                Directory.CreateDirectory(Path.Combine(root, "backups"));
                File.WriteAllBytes(Path.Combine(root, "backups", $"household-{householdId}-before-restore-{Guid.NewGuid():N}.json"), PortfolioBackup.Create(household.Profiles));
            }
            household.Profiles = profiles;
            household.Revision++;
            Write(database);
            return household.Revision;
        }
    }
    public List<MemberInfo> Members(ClaimsPrincipal principal, Guid householdId)
    {
        lock (gate)
        {
            var database = Read();
            var access = Access(principal, database, householdId);
            if (access.Role != HouseholdRole.Owner) throw new UnauthorizedAccessException("Only household owners can manage membership.");
            return database.Households.Single(h => h.Id == householdId).Members.Select(member =>
            {
                var user = database.Users.Single(user => user.Id == member.UserId);
                return new MemberInfo(user.ObjectId, user.Name, member.Role, user.Id);
            }).ToList();
        }
    }
    public void SetMember(ClaimsPrincipal principal, Guid householdId, Guid objectId, string name, HouseholdRole? role)
    {
        lock (gate)
        {
            var database = Read();
            var access = Access(principal, database, householdId);
            if (access.Role != HouseholdRole.Owner) throw new UnauthorizedAccessException("Only household owners can manage membership.");
            if (objectId == Guid.Empty || role.HasValue && !Enum.IsDefined(role.Value) || string.IsNullOrWhiteSpace(name) || name.Length > 100)
                throw new InvalidDataException("Enter a valid user Object ID, name, and role.");
            var household = database.Households.Single(h => h.Id == householdId);
            var user = database.Users.SingleOrDefault(user => user.TenantId == tenantId && user.ObjectId == objectId);
            if (user is null)
            {
                if (!role.HasValue) return;
                user = new ApplicationUser { TenantId = tenantId, ObjectId = objectId, Name = name.Trim() };
                database.Users.Add(user);
            }
            var membership = household.Members.SingleOrDefault(member => member.UserId == user.Id);
            if (membership?.Role == HouseholdRole.Owner && role != HouseholdRole.Owner && household.Members.Count(member => member.Role == HouseholdRole.Owner) == 1)
                throw new InvalidOperationException("A household must keep at least one owner.");
            if (!role.HasValue) household.Members.RemoveAll(member => member.UserId == user.Id);
            else if (membership is null) household.Members.Add(new() { UserId = user.Id, Role = role.Value });
            else membership.Role = role.Value;
            if (role.HasValue) user.Name = name.Trim();
            Write(database);
        }
    }
    private void Bootstrap(ClaimsPrincipal principal, (Guid Tenant, Guid Object) identity)
    {
        if (!Guid.TryParse(configuration["Households:BootstrapOwnerObjectId"], out var owner) || owner != identity.Object)
            throw new UnauthorizedAccessException("The household is not initialized. Configure its owner's Entra Object ID locally before signing in.");
        if (File.Exists(path))
        {
            using var input = File.OpenRead(path);
            var existing = DatabaseTransfer.Read(input);
            RelationalHouseholdStorage.Validate(existing);
            if (!existing.Users.Any(u => u.TenantId == identity.Tenant && u.ObjectId == identity.Object && existing.Households.Any(h => h.Members.Any(m => m.UserId == u.Id && m.Role == HouseholdRole.Owner))))
                throw new UnauthorizedAccessException("The configured bootstrap owner does not own the source household data.");
            Write(existing);
            return;
        }
        var legacyPath = Path.Combine(root, "portfolio.json");
        var legacy = File.Exists(legacyPath) ? PortfolioBackup.Read(File.ReadAllBytes(legacyPath)) : [];
        PortfolioBackup.Validate(legacy);
        var user = new ApplicationUser { TenantId = identity.Tenant, ObjectId = identity.Object, Name = principal.FindFirstValue("name") ?? "Household owner" };
        var household = new Household { Name = configuration["Households:BootstrapName"] ?? "Our household", Profiles = legacy, Members = [new() { UserId = user.Id, Role = HouseholdRole.Owner }] };
        // The legacy file remains untouched as a migration recovery copy.
        Write(new HouseholdDatabase { Users = [user], Households = [household] });
    }
    private HouseholdDatabase Read()
    {
        return storage.Read();
    }
    public List<HouseholdChoice> Households(ClaimsPrincipal principal)
    {
        var identity = Identity(principal);
        lock (gate)
        {
            var db = Read();
            var user = db.Users.SingleOrDefault(u => u.TenantId == identity.Tenant && u.ObjectId == identity.Object);
            return db.Households.Where(h => h.Members.Any(m => m.UserId == user?.Id)).Select(h => new HouseholdChoice(h.Id, h.Name)).ToList();
        }
    }
    public Guid UserId(ClaimsPrincipal principal)
    {
        var identity = Identity(principal);
        lock (gate) return Read().Users.Single(u => u.TenantId == identity.Tenant && u.ObjectId == identity.Object).Id;
    }
    public void RemoveMember(ClaimsPrincipal principal, Guid householdId, Guid userId)
    {
        lock (gate)
        {
            var db = Read();
            if (Access(principal, db, householdId).Role != HouseholdRole.Owner) throw new UnauthorizedAccessException();
            var h = db.Households.Single(h => h.Id == householdId);
            var member = h.Members.SingleOrDefault(m => m.UserId == userId);
            if (member?.Role == HouseholdRole.Owner && h.Members.Count(m => m.Role == HouseholdRole.Owner) == 1)
                throw new InvalidOperationException("A household must keep at least one owner.");
            h.Members.RemoveAll(m => m.UserId == userId);
            Write(db);
        }
    }
    public void SetMemberRole(ClaimsPrincipal principal, Guid householdId, Guid userId, HouseholdRole role)
    {
        if (!Enum.IsDefined(role)) throw new InvalidDataException("Choose a valid household role.");
        lock (gate)
        {
            var db = Read();
            if (Access(principal, db, householdId).Role != HouseholdRole.Owner) throw new UnauthorizedAccessException();
            var h = db.Households.Single(h => h.Id == householdId);
            var member = h.Members.SingleOrDefault(m => m.UserId == userId) ?? throw new InvalidOperationException("This person is no longer a member.");
            if (member.Role == HouseholdRole.Owner && role != HouseholdRole.Owner && h.Members.Count(m => m.Role == HouseholdRole.Owner) == 1)
                throw new InvalidOperationException("A household must keep at least one owner.");
            member.Role = role;
            Write(db);
        }
    }
    public string CreateInvitation(ClaimsPrincipal principal, Guid householdId)
    {
        lock (gate)
        {
            var db = Read();
            if (Access(principal, db, householdId).Role != HouseholdRole.Owner) throw new UnauthorizedAccessException();
            var identity = Identity(principal);
            var user = db.Users.Single(u => u.TenantId == identity.Tenant && u.ObjectId == identity.Object);
            db.Invitations.RemoveAll(i => i.ExpiresUtc <= DateTime.UtcNow);
            if (db.Invitations.Count(i => i.HouseholdId == householdId) >= 20) throw new InvalidOperationException("Revoke an existing invitation before creating another.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            db.Invitations.Add(new() { HouseholdId = householdId, CreatedBy = user.Id, TokenHash = HashInvitation(token), ExpiresUtc = DateTime.UtcNow.AddDays(7) });
            Write(db);
            return token;
        }
    }
    private static string HashInvitation(string token)
    {
        if (token.Length != 64 || token.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("This invitation is invalid, expired, or already used.");
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    }
    private static HouseholdInvitation ValidInvitation(HouseholdDatabase db, string token)
    {
        var hash = HashInvitation(token);
        var invitation = db.Invitations.SingleOrDefault(i => i.TokenHash == hash && i.ExpiresUtc > DateTime.UtcNow);
        if (invitation is null || !db.Households.Any(h => h.Id == invitation.HouseholdId && h.Members.Any(m => m.UserId == invitation.CreatedBy && m.Role == HouseholdRole.Owner)))
            throw new InvalidDataException("This invitation is invalid, expired, or already used.");
        return invitation;
    }
    public InvitationPreview PreviewInvitation(ClaimsPrincipal principal, string token)
    {
        Identity(principal);
        lock (gate)
        {
            var db = Read();
            var invitation = ValidInvitation(db, token);
            return new(db.Households.Single(h => h.Id == invitation.HouseholdId).Name, invitation.ExpiresUtc);
        }
    }
    public HouseholdAccess AcceptInvitation(ClaimsPrincipal principal, string token)
    {
        var identity = Identity(principal);
        lock (gate)
        {
            var db = Read();
            var invitation = ValidInvitation(db, token);
            var user = db.Users.SingleOrDefault(u => u.TenantId == identity.Tenant && u.ObjectId == identity.Object);
            if (user is null)
            {
                var name = principal.FindFirstValue("name")?.Trim();
                if (string.IsNullOrWhiteSpace(name)) name = "Household member";
                user = new() { TenantId = identity.Tenant, ObjectId = identity.Object, Name = name[..Math.Min(name.Length, 100)] };
                db.Users.Add(user);
            }
            var h = db.Households.Single(h => h.Id == invitation.HouseholdId);
            if (!h.Members.Any(m => m.UserId == user.Id)) h.Members.Add(new() { UserId = user.Id, Role = HouseholdRole.Member });
            db.Invitations.Remove(invitation);
            Write(db);
            return Access(principal, db, h.Id);
        }
    }
    public List<HouseholdInvitation> Invitations(ClaimsPrincipal principal, Guid householdId)
    {
        lock (gate)
        {
            var db = Read();
            if (Access(principal, db, householdId).Role != HouseholdRole.Owner) throw new UnauthorizedAccessException();
            return db.Invitations.Where(i => i.HouseholdId == householdId && i.ExpiresUtc > DateTime.UtcNow).ToList();
        }
    }
    public void RevokeInvitation(ClaimsPrincipal principal, Guid householdId, Guid invitationId)
    {
        lock (gate)
        {
            var db = Read();
            if (Access(principal, db, householdId).Role != HouseholdRole.Owner) throw new UnauthorizedAccessException();
            db.Invitations.RemoveAll(i => i.Id == invitationId && i.HouseholdId == householdId);
            Write(db);
        }
    }
    private void Write(HouseholdDatabase database)
    {
        storage.Write(database);
    }
}
