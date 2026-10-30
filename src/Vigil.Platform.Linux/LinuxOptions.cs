namespace Vigil.Platform.Linux;

/// <summary>Which invisible event is posted. Which one resets the idle counter without side effects has to be measured per machine.</summary>
public enum InjectionStrategy
{
    /// <summary>
    /// Move the pointer +1 px and straight back (zero net movement), the same as on macOS. A move of zero is not used
    /// because the Linux input core drops zero-valued relative events (drivers/input/input.c, input_get_disposition:
    /// the EV_REL case passes an event on only when its value is not 0), so on the uinput backend it would do nothing,
    /// and whether the X server or a compositor counts one as input is not documented. At a screen edge the first move
    /// is clamped and the way back then leaves the pointer one pixel off.
    /// </summary>
    MouseNudge,

    /// <summary>
    /// Post the key-up of F15, an unassigned key. On the uinput backend the key-down comes first, because the input core
    /// drops the release of a key that is not down (same function, EV_KEY case).
    /// </summary>
    F15KeyUp,
}

/// <summary>How input is injected. Idle time and lock state do not depend on it: they follow the session type.</summary>
public enum InputBackend
{
    /// <summary>X11 session: XTest. Wayland session: the desktop portal. Never uinput.</summary>
    Auto,

    /// <summary>XTest through libX11 and libXtst.</summary>
    X11,

    /// <summary>The xdg-desktop-portal RemoteDesktop interface; needs the consent given with <c>vigil permissions request</c>.</summary>
    Portal,

    /// <summary>A virtual device through /dev/uinput. Opt-in: the user sets up access to the device, Vigil never asks for root.</summary>
    Uinput,
}

public sealed class LinuxOptions
{
    public InjectionStrategy Strategy { get; set; } = InjectionStrategy.MouseNudge;

    public InputBackend Backend { get; set; } = InputBackend.Auto;
}
