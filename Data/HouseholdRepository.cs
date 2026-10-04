using System.Security.Claims;
using System.Text.Json.Serialization;

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
}
public sealed record HouseholdAccess(Guid Id, string Name, HouseholdRole Role, long Revision, List<Profile> Profiles);
public sealed record MemberInfo(Guid ObjectId, string Name, HouseholdRole Role);

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
            || !Guid.TryParse(principal.FindFirstValue("tid"), out var tenant) || tenant != tenantId
            || !Guid.TryParse(principal.FindFirstValue("oid"), out var subject) || subject == Guid.Empty
            || !long.TryParse(principal.FindFirstValue("netvalue:expires"), out var expires) || expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new UnauthorizedAccessException("Please sign in with an authorized account from this directory.");
        return (tenant, subject);
    }
    public HouseholdAccess Open(ClaimsPrincipal principal, Guid? householdId = null)
    {
        var identity = Identity(principal);
        lock (gate)
        {
            var database = Read();
            if (database.Households.Count == 0 && database.StorageRevision == 0) { Bootstrap(principal, identity); database = Read(); }
            return Access(principal, database, householdId);
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
                return new MemberInfo(user.ObjectId, user.Name, member.Role);
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
    private void Write(HouseholdDatabase database)
    {
        storage.Write(database);
    }
}
