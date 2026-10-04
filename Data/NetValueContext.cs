using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NetValue.Data;

public abstract class NetValueContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<StorageState> Storage => Set<StorageState>();
    public DbSet<ApplicationUser> Users => Set<ApplicationUser>();
    public DbSet<StoredHousehold> Households => Set<StoredHousehold>();
    public DbSet<StoredMember> Members => Set<StoredMember>();
    public DbSet<StoredProfile> Profiles => Set<StoredProfile>();
    public DbSet<StoredAccount> Accounts => Set<StoredAccount>();
    public DbSet<StoredMonth> Months => Set<StoredMonth>();
    public DbSet<StoredBalance> Balances => Set<StoredBalance>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<StorageState>().HasKey(x => x.Id);
        model.Entity<StorageState>().Property(x => x.Id).ValueGeneratedNever();
        model.Entity<ApplicationUser>().HasIndex(x => new { x.TenantId, x.ObjectId }).IsUnique();
        model.Entity<ApplicationUser>().Property(x => x.Name).HasMaxLength(100);
        model.Entity<StoredHousehold>().Property(x => x.Name).HasMaxLength(100);
        model.Entity<StoredMember>().HasKey(x => new { x.HouseholdId, x.UserId });
        model.Entity<StoredMember>().HasOne<StoredHousehold>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.NoAction);
        model.Entity<StoredMember>().HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        // Profile and account IDs are scoped to their parent, matching existing backup semantics.
        model.Entity<StoredProfile>().HasKey(x => new { x.HouseholdId, x.Id });
        model.Entity<StoredProfile>().Property(x => x.Name).HasMaxLength(60);
        model.Entity<StoredProfile>().HasOne<StoredHousehold>().WithMany().HasForeignKey(x => x.HouseholdId).OnDelete(DeleteBehavior.NoAction);
        model.Entity<StoredAccount>().HasKey(x => new { x.HouseholdId, x.ProfileId, x.Id });
        model.Entity<StoredAccount>().Property(x => x.Name).HasMaxLength(100);
        model.Entity<StoredAccount>().Property(x => x.Institution).HasMaxLength(100);
        model.Entity<StoredAccount>().HasOne<StoredProfile>().WithMany().HasForeignKey(x => new { x.HouseholdId, x.ProfileId }).OnDelete(DeleteBehavior.NoAction);
        model.Entity<StoredMonth>().HasKey(x => new { x.HouseholdId, x.ProfileId, x.Month });
        model.Entity<StoredMonth>().Property(x => x.Month).HasMaxLength(7);
        model.Entity<StoredMonth>().HasOne<StoredProfile>().WithMany().HasForeignKey(x => new { x.HouseholdId, x.ProfileId }).OnDelete(DeleteBehavior.NoAction);
        model.Entity<StoredBalance>().HasKey(x => new { x.HouseholdId, x.ProfileId, x.AccountId, x.Month });
        model.Entity<StoredBalance>().Property(x => x.Month).HasMaxLength(7);
        model.Entity<StoredBalance>().Property(x => x.Amount).HasPrecision(18, 2);
        model.Entity<StoredBalance>().HasOne<StoredAccount>().WithMany().HasForeignKey(x => new { x.HouseholdId, x.ProfileId, x.AccountId }).OnDelete(DeleteBehavior.NoAction);
        model.Entity<StoredBalance>().HasOne<StoredMonth>().WithMany().HasForeignKey(x => new { x.HouseholdId, x.ProfileId, x.Month }).OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class SqliteNetValueContext(DbContextOptions<SqliteNetValueContext> options) : NetValueContext(options);
public sealed class SqlServerNetValueContext(DbContextOptions<SqlServerNetValueContext> options) : NetValueContext(options);
public sealed class StorageState { public int Id { get; set; } public long Revision { get; set; } }
public sealed class StoredHousehold { public Guid Id { get; set; } public string Name { get; set; } = ""; public long Revision { get; set; } }
public sealed class StoredMember { public Guid HouseholdId { get; set; } public Guid UserId { get; set; } public HouseholdRole Role { get; set; } }
public sealed class StoredProfile { public Guid HouseholdId { get; set; } public Guid Id { get; set; } public string Name { get; set; } = ""; public int Position { get; set; } }
public sealed class StoredAccount { public Guid HouseholdId { get; set; } public Guid ProfileId { get; set; } public Guid Id { get; set; } public string Name { get; set; } = ""; public string Institution { get; set; } = ""; public AccountKind Kind { get; set; } public int Position { get; set; } }
public sealed class StoredMonth { public Guid HouseholdId { get; set; } public Guid ProfileId { get; set; } public string Month { get; set; } = ""; }
public sealed class StoredBalance { public Guid HouseholdId { get; set; } public Guid ProfileId { get; set; } public Guid AccountId { get; set; } public string Month { get; set; } = ""; public decimal Amount { get; set; } }

// Separate migration histories/models for each provider. Scaffolding never needs a live SQL server.
public sealed class SqliteDesignFactory : IDesignTimeDbContextFactory<SqliteNetValueContext>
{
    public SqliteNetValueContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SqliteNetValueContext>().UseSqlite("Data Source=App_Data/netvalue.db").Options);
}
public sealed class SqlServerDesignFactory : IDesignTimeDbContextFactory<SqlServerNetValueContext>
{
    public SqlServerNetValueContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SqlServerNetValueContext>().UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=NetValue;Trusted_Connection=True").Options);
}
