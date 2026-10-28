namespace Vigil.Tests;

/// <summary>A short-named temporary data directory (Unix socket paths are limited to about 100 bytes).</summary>
internal sealed class TempDataDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("vg");

    public TempDataDirectory() => Paths = new VigilPaths(_directory.FullName);

    public VigilPaths Paths { get; }

    public void WriteConfig(string json) => File.WriteAllText(Paths.ConfigPath, json);

    public void Dispose() => _directory.Delete(recursive: true);
}
