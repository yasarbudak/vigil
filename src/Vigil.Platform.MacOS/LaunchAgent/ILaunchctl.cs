namespace Vigil.Platform.MacOS.LaunchAgent;

public readonly record struct LaunchctlResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>The few launchctl operations Vigil needs, for the current user's GUI session. Behind an interface so tests never call launchctl.</summary>
public interface ILaunchctl
{
    /// <summary>Whether a job with this label is loaded in the user's GUI domain.</summary>
    bool IsLoaded(string label);

    /// <summary>Loads the plist into the user's GUI domain (starts it if RunAtLoad).</summary>
    LaunchctlResult Bootstrap(string plistPath);

    /// <summary>Unloads the job and stops it (SIGTERM first).</summary>
    LaunchctlResult Bootout(string label);
}
