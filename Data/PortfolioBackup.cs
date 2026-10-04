using System.Globalization;
using System.Text.Json;

namespace NetValue.Data;

public static class PortfolioBackup
{
    public const long MaxBytes = 10 * 1024 * 1024;
    private sealed record Backup(string Format, int Version, DateTime CreatedAtUtc, List<Profile> Profiles);
    public static byte[] Create(List<Profile> profiles) => JsonSerializer.SerializeToUtf8Bytes(
        new Backup("NetValue", 1, DateTime.UtcNow, profiles), new JsonSerializerOptions { WriteIndented = true });

    public static List<Profile> Read(byte[] bytes)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("The backup must be 10 MB or smaller.");
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            JsonElement data;
            if (root.ValueKind == JsonValueKind.Array) data = root; // Original portfolio.json files are supported.
            else
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Format", out var format) || format.GetString() != "NetValue"
                    || !root.TryGetProperty("Version", out var version) || version.GetInt32() != 1
                    || !root.TryGetProperty("Profiles", out data)) throw new InvalidDataException("This is not a supported NetValue backup (version 1).");
            }
            if (data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("The backup's profiles must be a list.");
            foreach (var profile in data.EnumerateArray())
            {
                Require(profile, "Id", "Name", "Accounts", "Months");
                if (profile.GetProperty("Accounts").ValueKind != JsonValueKind.Array) throw new InvalidDataException("Each profile must contain an account list.");
                foreach (var account in profile.GetProperty("Accounts").EnumerateArray()) Require(account, "Id", "Name", "Kind");
            }
            var profiles = data.Deserialize<List<Profile>>() ?? throw new InvalidDataException("The backup has no profile list.");
            Validate(profiles);
            return profiles;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("The backup contains invalid or unreadable data.", exception);
        }
    }
    private static void Require(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object || fields.Any(field => !element.TryGetProperty(field, out _)))
            throw new InvalidDataException("The backup is missing required profile or account fields.");
    }
    public static void Validate(List<Profile> profiles)
    {
        var ids = new HashSet<Guid>();
        foreach (var profile in profiles)
        {
            if (profile is null || profile.Id == Guid.Empty || !ids.Add(profile.Id) || string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 60 || profile.Accounts is null || profile.Months is null)
                throw new InvalidDataException("Profiles must have unique IDs, names of 1–60 characters, accounts, and monthly data.");
            var accounts = new HashSet<Guid>();
            foreach (var account in profile.Accounts)
            {
                if (account is null || account.Id == Guid.Empty || !accounts.Add(account.Id) || string.IsNullOrWhiteSpace(account.Name) || account.Name.Length > 100 || !Enum.IsDefined(account.Kind))
                    throw new InvalidDataException("Accounts must have unique IDs, valid categories, and names of 1–100 characters.");
                account.Institution ??= "";
                if (account.Institution.Length > 100) throw new InvalidDataException("Institution names must be 100 characters or fewer.");
            }
            foreach (var (month, balances) in profile.Months)
            {
                if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) || balances is null || balances.Any(entry => !accounts.Contains(entry.Key) || entry.Value < 0 || entry.Value > 9999999999999999.99m || decimal.Round(entry.Value, 2) != entry.Value))
                    throw new InvalidDataException("Monthly balances must use valid months, existing accounts, and non-negative amounts with at most two decimal places.");
            }
        }
    }
}
