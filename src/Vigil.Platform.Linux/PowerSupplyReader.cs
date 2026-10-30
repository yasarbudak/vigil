namespace Vigil.Platform.Linux;

/// <summary>
/// Reads the attributes <see cref="PowerSupplyClassifier"/> needs from <c>/sys/class/power_supply/&lt;supply&gt;/</c>:
/// <c>type</c>, <c>online</c>, <c>status</c> and <c>scope</c>, each a one-line text file. Reads nothing else. The directory
/// is a parameter so tests use a copy.
/// </summary>
internal sealed class PowerSupplyReader(string root = PowerSupplyReader.DefaultRoot)
{
    internal const string DefaultRoot = "/sys/class/power_supply";

    /// <returns>The supplies, which may be none on a desktop; null when the directory cannot be read at all.</returns>
    public IReadOnlyList<PowerSupply>? Read()
    {
        string[] directories;
        try
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            directories = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var supplies = new List<PowerSupply>(directories.Length);
        foreach (var directory in directories)
        {
            supplies.Add(new PowerSupply(
                ReadText(directory, "type"),
                int.TryParse(ReadText(directory, "online"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var online) ? online : null,
                ReadText(directory, "status"),
                ReadText(directory, "scope")));
        }

        return supplies;
    }

    private static string? ReadText(string directory, string attribute)
    {
        try
        {
            var path = Path.Combine(directory, attribute);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Some attributes return an error while the device is busy or asleep; unknown, not a failure.
            return null;
        }
    }
}
