using System.Runtime.Versioning;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.MacOS;

/// <summary>Posts one invisible event through the HID event tap using the configured strategy.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacOSInputInjector(MacOSOptions options) : IInputInjector
{
    public InjectionResult Inject()
    {
        using var source = NativeMethods.CGEventSourceCreate(NativeMethods.EventSourceStateHidSystemState);
        if (source.IsInvalid)
        {
            return InjectionResult.Failure("Could not create a CoreGraphics event source.");
        }

        return options.Strategy switch
        {
            InjectionStrategy.MouseNudge => NudgeMouse(source),
            InjectionStrategy.F15KeyUp => PostF15KeyUp(source),
            _ => InjectionResult.Failure($"Unknown injection strategy '{options.Strategy}'."),
        };
    }

    private static InjectionResult NudgeMouse(CFTypeHandle source)
    {
        using var current = NativeMethods.CGEventCreate(nint.Zero);
        if (current.IsInvalid)
        {
            return InjectionResult.Failure("Could not read the current pointer position.");
        }

        var home = NativeMethods.CGEventGetLocation(current);
        var nudged = new CGPoint(home.X + 1, home.Y);

        // Two moves, zero net displacement: out by one pixel, then back to where the pointer was.
        return PostMouseMove(source, nudged) is { Posted: false } failure
            ? failure
            : PostMouseMove(source, home);
    }

    private static InjectionResult PostMouseMove(CFTypeHandle source, CGPoint position)
    {
        using var move = NativeMethods.CGEventCreateMouseEvent(
            source, NativeMethods.EventMouseMoved, position, NativeMethods.MouseButtonLeft);
        if (move.IsInvalid)
        {
            return InjectionResult.Failure("Could not create a mouse event.");
        }

        NativeMethods.CGEventPost(NativeMethods.HidEventTap, move);
        return InjectionResult.Success;
    }

    private static InjectionResult PostF15KeyUp(CFTypeHandle source)
    {
        using var keyUp = NativeMethods.CGEventCreateKeyboardEvent(source, NativeMethods.KeyCodeF15, keyDown: false);
        if (keyUp.IsInvalid)
        {
            return InjectionResult.Failure("Could not create a keyboard event.");
        }

        NativeMethods.CGEventPost(NativeMethods.HidEventTap, keyUp);
        return InjectionResult.Success;
    }
}
