using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.Windows;

/// <summary>
/// Posts one invisible event with SendInput using the configured strategy.
/// </summary>
/// <remarks>
/// <see cref="InjectionStrategy.MouseNudge"/> is a relative move of zero pixels, not a move out and back. A relative
/// move is scaled by the pointer speed and the two acceleration thresholds ("up to four times", MOUSEINPUT remarks),
/// and the documentation does not say how fractions are rounded, so a +1/-1 pair is not documented to cancel out. An
/// absolute move back to the position GetCursorPos returned would need the pixel position turned into the normalized
/// 0 to 65535 range that MOUSEEVENTF_ABSOLUTE uses (relative to the primary monitor or the whole virtual desktop), a
/// conversion the documentation gives no exact rule for, and GetCursorPos needs the input desktop to be the current
/// one. Zero times any scale is zero, and no position has to be read. UIPI applies (see <see cref="WindowsPermissions"/>).
/// https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsInputInjector(WindowsOptions options) : IInputInjector
{
    public InjectionResult Inject() => options.Strategy switch
    {
        InjectionStrategy.MouseNudge => Send(Input.MouseMoveWithoutDisplacement()),
        InjectionStrategy.F15KeyUp => Send(Input.KeyUp(Win32Constants.VirtualKeyF15)),
        _ => InjectionResult.Failure($"Unknown injection strategy '{options.Strategy}'."),
    };

    private static unsafe InjectionResult Send(Input input)
    {
        // One event per call. The call returns how many events it inserted; anything but 1 is a failure.
        var inserted = NativeMethods.SendInput(1, &input, Unsafe.SizeOf<Input>());
        return inserted == 1 ? InjectionResult.Success : InjectionResult.Failure(DescribeFailure(Marshal.GetLastPInvokeError()));
    }

    /// <summary>
    /// A return value of zero means the input was blocked by another thread or by UIPI, and the documentation says
    /// that "neither GetLastError nor the return value will indicate the failure was caused by UIPI blocking", so error
    /// code 0 gets the explanation of both.
    /// </summary>
    internal static string DescribeFailure(int lastError) => lastError == 0
        ? "SendInput inserted no event: the input was blocked, for example by UIPI because the foreground window runs with higher rights than Vigil."
        : $"SendInput failed: {new Win32Exception(lastError).Message} (error {lastError}).";
}
