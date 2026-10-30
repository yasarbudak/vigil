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
    public static VigilSettings Load(string configPath)
    {
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(configPath, optional: true, reloadOnChange: false)
                .Build();

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

            throw new ConfigurationException($"Cannot use {configPath}: {root.Message}", ex);
        }
    }

    /// <summary>Like <see cref="Load"/>, and also throws when a value is invalid.</summary>
    public static VigilSettings LoadValidated(string configPath)
    {
        var settings = Load(configPath);
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new ConfigurationException($"Invalid configuration in {configPath}: " + string.Join(" ", errors));
        }

        return settings;
    }
}
