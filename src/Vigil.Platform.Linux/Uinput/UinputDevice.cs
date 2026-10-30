using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Vigil.Core.Abstractions;

namespace Vigil.Platform.Linux.Uinput;

/// <summary>
/// A virtual input device created through uinput for the lifetime of this object, and the events written to it. The device
/// has only what the strategy posts: a pointer (REL_X, REL_Y and BTN_LEFT, which is what Documentation/input/uinput.rst
/// enables for "a virtual device that behaves like a physical mouse") for MouseNudge, or the F15 key for F15KeyUp. Created
/// once and kept: the documentation says userspace needs a moment to notice a new device and to open it, and events sent
/// before that are not seen, so the first use waits <see cref="SettleDelay"/>.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class UinputDevice : IDisposable
{
    /// <summary>The documentation's example waits one second after UI_DEV_CREATE "so that userspace has time to detect, initialize the new device".</summary>
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    private readonly SafeFileHandle _handle;
    private readonly int _fd;
    private bool _created;

    private UinputDevice(SafeFileHandle handle)
    {
        _handle = handle;
        _fd = (int)handle.DangerousGetHandle();
    }

    /// <exception cref="LinuxUnavailableException">The device cannot be opened or set up; the message says what to do.</exception>
    public static UinputDevice Create(string path, InjectionStrategy strategy)
    {
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LinuxUnavailableException($"Cannot open {path} for writing: {ex.Message}", ex);
        }

        var device = new UinputDevice(handle);
        try
        {
            device.Setup(strategy);
            return device;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private unsafe void Setup(InjectionStrategy strategy)
    {
        switch (strategy)
        {
            case InjectionStrategy.MouseNudge:
                Check(UinputNativeMethods.Ioctl(_fd, UinputProtocol.SetEvBit, UinputProtocol.EvKey), "UI_SET_EVBIT EV_KEY");
                Check(UinputNativeMethods.Ioctl(_fd, UinputProtocol.SetKeyBit, UinputProtocol.BtnLeft), "UI_SET_KEYBIT BTN_LEFT");
                Check(UinputNativeMethods.Ioctl(_fd, UinputProtocol.SetEvBit, UinputProtocol.EvRel), "UI_SET_EVBIT EV_REL");
                Check(UinputNativeMethods.Ioctl(_fd, UinputProtocol.SetRelBit, UinputProtocol.RelX), "UI_SET_RELBIT REL_X");
                Check(UinputNativeMethods.Ioctl(_fd, UinputProtocol.SetRelBit, UinputProtocol.RelY), "UI_SET_RELBIT REL_Y");
                break;
            case InjectionStrategy.F15KeyUp:
                Check(UinputNativeMethods.Ioctl(_fd, UinputProtocol.SetEvBit, UinputProtocol.EvKey), "UI_SET_EVBIT EV_KEY");
                Check(UinputNativeMethods.Ioctl(_fd, UinputProtocol.SetKeyBit, UinputProtocol.KeyF15), "UI_SET_KEYBIT KEY_F15");
                break;
            default:
                throw new LinuxUnavailableException($"Unknown injection strategy '{strategy}'.");
        }

        var setup = default(UinputSetup);
        setup.Id.BusType = UinputProtocol.BusVirtual;
        var name = Encoding.ASCII.GetBytes(UinputProtocol.DeviceName);
        for (var i = 0; i < name.Length && i < UinputProtocol.MaxNameSize - 1; i++)
        {
            setup.Name[i] = name[i];
        }

        Check(UinputNativeMethods.Ioctl(_fd, UinputProtocol.DevSetup, &setup), "UI_DEV_SETUP");
        Check(UinputNativeMethods.Ioctl(_fd, UinputProtocol.DevCreate), "UI_DEV_CREATE");
        _created = true;
    }

    /// <summary>Writes the events in one write call, which uinput processes in order.</summary>
    public unsafe InjectionResult Write(byte[] events)
    {
        fixed (byte* pointer = events)
        {
            var written = UinputNativeMethods.Write(_fd, pointer, (nuint)events.Length);
            return written == events.Length
                ? InjectionResult.Success
                : InjectionResult.Failure(written < 0
                    ? $"Writing to uinput failed: {new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message}."
                    : $"Writing to uinput wrote {written} of {events.Length} bytes.");
        }
    }

    public void Dispose()
    {
        if (_created)
        {
            _ = UinputNativeMethods.Ioctl(_fd, UinputProtocol.DevDestroy);
            _created = false;
        }

        _handle.Dispose();
    }

    private static void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new LinuxUnavailableException(
                $"uinput refused {what}: {new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message}.");
        }
    }
}
