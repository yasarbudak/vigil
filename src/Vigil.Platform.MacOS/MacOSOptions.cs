namespace Vigil.Platform.MacOS;

/// <summary>Which invisible event is posted. Which one resets the idle counter without side effects is measured per machine.</summary>
public enum InjectionStrategy
{
    /// <summary>Move the pointer +1 px and straight back (zero net movement).</summary>
    MouseNudge,

    /// <summary>Post only the key-up of F15, an unassigned key.</summary>
    F15KeyUp,
}

public sealed class MacOSOptions
{
    public InjectionStrategy Strategy { get; set; } = InjectionStrategy.MouseNudge;
}
