using System.Text;
using Microsoft.Extensions.Configuration;
using Vigil.Core.Options;
using Vigil.Platform;
using Vigil.Platform.Linux;
using Vigil.Platform.MacOS;
using Vigil.Platform.Windows;

namespace Vigil.Configuration;

/// <summary>The bound contents of config.json.</summary>
internal sealed record VigilSettings(VigilOptions Vigil, MacOSOptions MacOS, WindowsOptions Windows, LinuxOptions Linux)
{
    /// <summary>What <see cref="PlatformServices.Create"/> needs (the caller adds the data directory).</summary>
    public PlatformOptions Platform => new(MacOS, Windows, Linux);

    public IReadOnlyList<string> Validate() => Vigil.Validate();
}

/// <summary>config.json exists but cannot be used: malformed JSON, unreadable, wrong value types, or invalid values.</summary>
internal sealed class ConfigurationException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The single place that reads and binds config.json. Layout: a "Vigil" section (<see cref="VigilOptions"/>),
/// a "MacOS" section (<see cref="MacOSOptions"/>), a "Windows" section (<see cref="WindowsOptions"/>) and a "Linux" section
/// (<see cref="LinuxOptions"/>). Every section is bound on every platform, so a bad value in one fails the load
/// everywhere. A missing file means defaults.
/// </summary>
internal static class VigilConfiguration
{
    private const string VigilSection = "Vigil";
    private const string MacOSSection = "MacOS";
    private const string WindowsSection = "Windows";
    private const string LinuxSection = "Linux";

    /// <summary>Reads and binds the file without validating values. Throws <see cref="ConfigurationException"/> if it cannot.</summary>
    public static VigilSettings Load(string configPath) =>
        Bind(configPath, builder => builder.AddJsonFile(configPath, optional: true, reloadOnChange: false));

    /// <summary>
    /// Binds configuration text exactly as <see cref="Load"/> binds the file, without validating values. The tray uses it to
    /// check what it is about to write with the daemon's own loader, so a saved file is one the daemon accepts.
    /// </summary>
    /// <param name="origin">Names the source in error messages (the path the text is meant for).</param>
    public static VigilSettings LoadFromJson(string json, string origin) =>
        Bind(origin, builder => builder.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))));

    private static VigilSettings Bind(string origin, Action<ConfigurationBuilder> addSource)
    {
        try
        {
            var builder = new ConfigurationBuilder();
            addSource(builder);
            var configuration = builder.Build();

            return new VigilSettings(
                configuration.GetSection(VigilSection).Get<VigilOptions>() ?? new VigilOptions(),
                configuration.GetSection(MacOSSection).Get<MacOSOptions>() ?? new MacOSOptions(),
                configuration.GetSection(WindowsSection).Get<WindowsOptions>() ?? new WindowsOptions(),
                configuration.GetSection(LinuxSection).Get<LinuxOptions>() ?? new LinuxOptions());
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or InvalidOperationException
            or IOException or UnauthorizedAccessException)
        {
            // The JSON provider wraps the parser error (which carries line and position) as an inner exception.
            var root = ex;
            while (root.InnerException is { } inner)
            {
                root = inner;
            }

            // The binder names the key in its own message ("Failed to convert configuration value at 'Vigil:Threshold' to type ...")
            // and the conversion error names the text; both are needed to find the mistake.
            var detail = ex is InvalidOperationException && !ReferenceEquals(ex, root) ? $"{ex.Message} {root.Message}" : root.Message;
            throw new ConfigurationException($"Cannot use {origin}: {detail}", ex);
        }
    }

    /// <summary>Like <see cref="Load"/>, and also throws when a value is invalid.</summary>
    public static VigilSettings LoadValidated(string configPath) => Validated(Load(configPath), configPath);

    /// <summary>Like <see cref="LoadFromJson"/>, and also throws when a value is invalid.</summary>
    public static VigilSettings LoadFromJsonValidated(string json, string origin) => Validated(LoadFromJson(json, origin), origin);

    private static VigilSettings Validated(VigilSettings settings, string origin)
    {
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new ConfigurationException($"Invalid configuration in {origin}: " + string.Join(" ", errors));
        }

        return settings;
    }
}
