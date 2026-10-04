using System.Text.Json;

namespace NetValue.Data;

public enum AccountKind { Cash, Investment, Property, ShortTermDebt, LongTermDebt }
public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Institution { get; set; } = "";
    public AccountKind Kind { get; set; }
}
public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<Account> Accounts { get; set; } = [];
    public Dictionary<string, Dictionary<Guid, decimal>> Months { get; set; } = [];
}
public sealed class PortfolioStore
{
    private readonly string path;
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public PortfolioStore(IWebHostEnvironment environment)
    {
        path = Path.Combine(environment.ContentRootPath, "App_Data", "portfolio.json");
    }
    public List<Profile> Load()
    {
        lock (gate)
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<Profile>>(File.ReadAllText(path)) ?? []
                : [];
    }
    public void Save(List<Profile> profiles)
    {
        lock (gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(profiles, Options));
            File.Move(temporary, path, true);
        }
    }
    public void Restore(List<Profile> profiles)
    {
        PortfolioBackup.Validate(profiles);
        lock (gate)
        {
            if (File.Exists(path))
            {
                var backups = Path.Combine(Path.GetDirectoryName(path)!, "backups");
                Directory.CreateDirectory(backups);
                File.Copy(path, Path.Combine(backups, $"portfolio-before-restore-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json"));
            }
            Save(profiles);
        }
    }
}
