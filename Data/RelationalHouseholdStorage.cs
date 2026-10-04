using System.Data;
using Microsoft.EntityFrameworkCore;

namespace NetValue.Data;

public sealed class RelationalHouseholdStorage
{
    private readonly Func<NetValueContext> create;
    private readonly bool autoMigrate;
    private readonly object gate = new();
    private bool initialized;

    public RelationalHouseholdStorage(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        autoMigrate = environment.IsDevelopment();
        if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment()) throw new InvalidOperationException("Use Database:Provider=SqlServer outside Development.");
            var directory = configuration["Database:DataDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data");
            directory = Path.GetFullPath(directory, environment.ContentRootPath);
            Directory.CreateDirectory(directory);
            var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "netvalue.db"), ForeignKeys = true }.ToString();
            create = () => new SqliteNetValueContext(new DbContextOptionsBuilder<SqliteNetValueContext>().UseSqlite(connection).Options);
        }
        else if (provider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            var connection = configuration.GetConnectionString("NetValue");
            if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("Configure ConnectionStrings:NetValue outside source control for SQL Server.");
            create = () => new SqlServerNetValueContext(new DbContextOptionsBuilder<SqlServerNetValueContext>().UseSqlServer(connection).Options);
        }
        else throw new InvalidOperationException("Database:Provider must be Sqlite or SqlServer.");
    }

    public void Migrate()
    {
        lock (gate) { using var db = create(); db.Database.Migrate(); initialized = true; }
    }

    private NetValueContext Open()
    {
        lock (gate)
        {
            if (!initialized)
            {
                if (autoMigrate) Migrate();
                else
                {
                    using var check = create();
                    if (check.Database.GetPendingMigrations().Any()) throw new InvalidOperationException("Apply database migrations before starting NetValue.");
                    initialized = true;
                }
            }
        }
        return create();
    }

    public HouseholdDatabase Read()
    {
        using var db = Open();
        using var transaction = db.Database.BeginTransaction(IsolationLevel.Serializable);
        var result = new HouseholdDatabase { StorageRevision = db.Storage.SingleOrDefault(x => x.Id == 1)?.Revision ?? 0, Users = db.Users.AsNoTracking().ToList() };
        var members = db.Members.AsNoTracking().ToList();
        var profiles = db.Profiles.AsNoTracking().OrderBy(x => x.Position).ToList();
        var accounts = db.Accounts.AsNoTracking().OrderBy(x => x.Position).ToList();
        var months = db.Months.AsNoTracking().ToList();
        var balances = db.Balances.AsNoTracking().ToList();
        foreach (var h in db.Households.AsNoTracking().ToList())
        {
            var household = new Household { Id = h.Id, Name = h.Name, Revision = h.Revision, Members = members.Where(x => x.HouseholdId == h.Id).Select(x => new HouseholdMembership { UserId = x.UserId, Role = x.Role }).ToList() };
            foreach (var p in profiles.Where(x => x.HouseholdId == h.Id))
            {
                var profile = new Profile { Id = p.Id, Name = p.Name, Accounts = accounts.Where(x => x.HouseholdId == h.Id && x.ProfileId == p.Id).Select(x => new Account { Id = x.Id, Name = x.Name, Institution = x.Institution, Kind = x.Kind }).ToList() };
                foreach (var month in months.Where(x => x.HouseholdId == h.Id && x.ProfileId == p.Id))
                    profile.Months[month.Month] = balances.Where(x => x.HouseholdId == h.Id && x.ProfileId == p.Id && x.Month == month.Month).ToDictionary(x => x.AccountId, x => x.Amount);
                household.Profiles.Add(profile);
            }
            result.Households.Add(household);
        }
        transaction.Commit();
        return result;
    }

    public void Write(HouseholdDatabase data)
    {
        Validate(data);
        using var db = Open();
        using var transaction = db.Database.BeginTransaction(IsolationLevel.Serializable);
        if (data.StorageRevision == 0 && !db.Storage.Any())
        {
            db.Storage.Add(new StorageState { Id = 1, Revision = 1 });
            db.SaveChanges();
        }
        else if (db.Storage.Where(x => x.Id == 1 && x.Revision == data.StorageRevision).ExecuteUpdate(s => s.SetProperty(x => x.Revision, x => x.Revision + 1)) != 1)
            throw new InvalidOperationException("Data was updated by another session. Reload the page before editing again.");

        // Synchronize changed rows only; the revision also protects membership edits
        // against other application instances. All changes commit together.
        db.ChangeTracker.Clear();
        var households = new List<StoredHousehold>();
        var members = new List<StoredMember>();
        var profiles = new List<StoredProfile>();
        var accounts = new List<StoredAccount>();
        var months = new List<StoredMonth>();
        var balances = new List<StoredBalance>();
        foreach (var h in data.Households)
        {
            households.Add(new() { Id = h.Id, Name = h.Name, Revision = h.Revision });
            members.AddRange(h.Members.Select(m => new StoredMember { HouseholdId = h.Id, UserId = m.UserId, Role = m.Role }));
            for (var i = 0; i < h.Profiles.Count; i++)
            {
                var p = h.Profiles[i];
                profiles.Add(new() { HouseholdId = h.Id, Id = p.Id, Name = p.Name, Position = i });
                accounts.AddRange(p.Accounts.Select((a, index) => new StoredAccount { HouseholdId = h.Id, ProfileId = p.Id, Id = a.Id, Name = a.Name, Institution = a.Institution, Kind = a.Kind, Position = index }));
                foreach (var month in p.Months)
                {
                    months.Add(new() { HouseholdId = h.Id, ProfileId = p.Id, Month = month.Key });
                    balances.AddRange(month.Value.Select(b => new StoredBalance { HouseholdId = h.Id, ProfileId = p.Id, AccountId = b.Key, Month = month.Key, Amount = b.Value }));
                }
            }
        }
        Sync(db, db.Users, data.Users, x => x.Id);
        Sync(db, db.Households, households, x => x.Id);
        Sync(db, db.Members, members, x => (x.HouseholdId, x.UserId));
        Sync(db, db.Profiles, profiles, x => (x.HouseholdId, x.Id));
        Sync(db, db.Accounts, accounts, x => (x.HouseholdId, x.ProfileId, x.Id));
        Sync(db, db.Months, months, x => (x.HouseholdId, x.ProfileId, x.Month));
        Sync(db, db.Balances, balances, x => (x.HouseholdId, x.ProfileId, x.AccountId, x.Month));
        db.SaveChanges();
        transaction.Commit();
        data.StorageRevision++;
    }

    private static void Sync<T, TKey>(NetValueContext db, DbSet<T> set, IEnumerable<T> desired, Func<T, TKey> key) where T : class where TKey : notnull
    {
        var existing = set.ToList().ToDictionary(key);
        foreach (var row in desired)
        {
            if (existing.Remove(key(row), out var current)) db.Entry(current).CurrentValues.SetValues(row);
            else set.Add(row);
        }
        set.RemoveRange(existing.Values);
    }

    public static void Validate(HouseholdDatabase data)
    {
        if (data.Version != 1 || data.Users is null || data.Households is null) throw new InvalidDataException("Unsupported or invalid household data.");
        if (data.Households.Any(h => h is null) || data.Users.Any(u => u is null || u.Id == Guid.Empty || u.TenantId == Guid.Empty || u.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(u.Name) || u.Name.Length > 100)
            || data.Users.Select(u => u.Id).Distinct().Count() != data.Users.Count
            || data.Users.Select(u => (u.TenantId, u.ObjectId)).Distinct().Count() != data.Users.Count
            || data.Households.Select(h => h.Id).Distinct().Count() != data.Households.Count)
            throw new InvalidDataException("Invalid or duplicate household identities.");
        foreach (var h in data.Households)
        {
            if (h.Id == Guid.Empty || string.IsNullOrWhiteSpace(h.Name) || h.Name.Length > 100 || h.Revision < 0 || h.Members is null
                || h.Members.Any(m => m is null) || !h.Members.Any(m => m.Role == HouseholdRole.Owner)
                || h.Members.Select(m => m.UserId).Distinct().Count() != h.Members.Count
                || h.Members.Any(m => !Enum.IsDefined(m.Role) || !data.Users.Any(u => u.Id == m.UserId)))
                throw new InvalidDataException("Invalid household or membership.");
            if (h.Profiles is null) throw new InvalidDataException("Household profiles are missing.");
            PortfolioBackup.Validate(h.Profiles);
        }
    }
}
