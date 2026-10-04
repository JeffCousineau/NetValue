using System.Text.Json;

namespace NetValue.Data;

// Operator-only offline commands: never exposed as HTTP endpoints or browser uploads.
public static class DatabaseTransfer
{
    public static void Export(RelationalHouseholdStorage storage, string path)
    {
        var data = storage.Read();
        RelationalHouseholdStorage.Validate(data);
        using var output = new FileStream(Path.GetFullPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, data, new JsonSerializerOptions { WriteIndented = true });
    }

    public static void Import(RelationalHouseholdStorage storage, string path)
    {
        var file = new FileInfo(path);
        if (file.Length > 50 * 1024 * 1024) throw new InvalidDataException("Operator backups are limited to 50 MB.");
        using var input = file.OpenRead();
        var source = Read(input);
        RelationalHouseholdStorage.Validate(source);
        if (source.Households.Count == 0) throw new InvalidDataException("An operator import must contain at least one household.");
        var target = storage.Read();
        if (target.Households.Count != 0 || target.Users.Count != 0) throw new InvalidOperationException("Operator import requires an empty database. Existing data will not be overwritten.");
        source.StorageRevision = target.StorageRevision;
        storage.Write(source);
    }

    public static HouseholdDatabase Read(Stream input)
    {
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var number) || number != 1
            || !root.TryGetProperty("Users", out var users) || users.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("Households", out var households) || households.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Choose a version 1 operator/household backup, not a financial backup.");
        return root.Deserialize<HouseholdDatabase>() ?? throw new InvalidDataException("Invalid operator backup.");
    }
}
