namespace Vigil.Platform.Windows;

/// <summary>Which invisible event is posted. Which one resets the idle counter without side effects has to be measured per machine.</summary>
public enum InjectionStrategy
{
    /// <summary>
    /// Post one relative mouse move of zero pixels. The pointer does not move at all, whatever the pointer speed or
    /// acceleration settings are, so the net movement is zero by construction. Whether Windows counts such an event
    /// as input is checked at run time by the idle verification; if it does not, use <see cref="F15KeyUp"/>.
    /// </summary>
    MouseNudge,

    /// <summary>Post only the key-up of F15, an unassigned key.</summary>
    F15KeyUp,
}

public sealed class WindowsOptions
{
    public InjectionStrategy Strategy { get; set; } = InjectionStrategy.MouseNudge;
}
