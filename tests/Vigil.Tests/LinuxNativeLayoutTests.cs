using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vigil.Platform.Linux;
using Vigil.Platform.Linux.Uinput;
using Vigil.Platform.Linux.X11;

namespace Vigil.Tests;

/// <summary>
/// The structures and numbers handed to the X libraries and to the kernel must be exactly what the headers define. Pure
/// layout and arithmetic checks: nothing here calls a native function, and they run on every OS (on the 64-bit LP64 layout
/// of the two Linux targets, which is why a few checks are skipped on a 32-bit process).
/// </summary>
public class LinuxNativeLayoutTests
{
    private static bool Lp64 => nint.Size == 8 && !OperatingSystem.IsWindows(); // Windows has a 4-byte unsigned long.

    [Fact]
    public void XScreenSaverInfo_follows_scrnsaver_h_on_LP64()
    {
        Assert.SkipUnless(Lp64, "Window and unsigned long are 8 bytes on LP64 only.");

        Assert.Equal(40, Unsafe.SizeOf<XScreenSaverInfo>());
        Assert.Equal(0, (int)Marshal.OffsetOf<XScreenSaverInfo>(nameof(XScreenSaverInfo.Window)));
        Assert.Equal(8, (int)Marshal.OffsetOf<XScreenSaverInfo>(nameof(XScreenSaverInfo.State)));
        Assert.Equal(12, (int)Marshal.OffsetOf<XScreenSaverInfo>(nameof(XScreenSaverInfo.Kind)));
        Assert.Equal(16, (int)Marshal.OffsetOf<XScreenSaverInfo>(nameof(XScreenSaverInfo.TilOrSince)));
        Assert.Equal(24, (int)Marshal.OffsetOf<XScreenSaverInfo>(nameof(XScreenSaverInfo.Idle)));
        Assert.Equal(32, (int)Marshal.OffsetOf<XScreenSaverInfo>(nameof(XScreenSaverInfo.EventMask)));
    }

    [Fact]
    public void XErrorEvent_follows_Xlib_h_on_LP64()
    {
        Assert.SkipUnless(Lp64, "Pointers and unsigned long are 8 bytes on LP64 only.");

        Assert.Equal(0, (int)Marshal.OffsetOf<XErrorEvent>(nameof(XErrorEvent.Type)));
        Assert.Equal(8, (int)Marshal.OffsetOf<XErrorEvent>(nameof(XErrorEvent.Display)));
        Assert.Equal(16, (int)Marshal.OffsetOf<XErrorEvent>(nameof(XErrorEvent.ResourceId)));
        Assert.Equal(24, (int)Marshal.OffsetOf<XErrorEvent>(nameof(XErrorEvent.Serial)));
        Assert.Equal(32, (int)Marshal.OffsetOf<XErrorEvent>(nameof(XErrorEvent.ErrorCode)));
        Assert.Equal(33, (int)Marshal.OffsetOf<XErrorEvent>(nameof(XErrorEvent.RequestCode)));
        Assert.Equal(34, (int)Marshal.OffsetOf<XErrorEvent>(nameof(XErrorEvent.MinorCode)));
    }

    [Fact]
    public void F15_has_the_keysym_of_keysymdef_h_and_the_evdev_code_of_input_event_codes_h()
    {
        Assert.Equal(0xffcc, Keysyms.F15); // #define XK_F15 0xffcc
        Assert.Equal(185, UinputProtocol.KeyF15); // #define KEY_F15 185
    }

    [Fact]
    public void The_X11_libraries_are_the_sonames_the_distributions_install()
    {
        Assert.Equal("libX11.so.6", X11Libraries.Xlib);
        Assert.Equal("libXss.so.1", X11Libraries.Xss);
        Assert.Equal("libXtst.so.6", X11Libraries.Xtst);
    }

    [Fact]
    public void A_missing_library_is_named_with_its_package()
    {
        Assert.Equal("libXss.so.1", X11Libraries.Missing(["libX11.so.6", "libXss.so.1"], name => name == "libX11.so.6"));
        Assert.Null(X11Libraries.Missing(["libX11.so.6"], _ => true));
        Assert.Contains("libxss1", X11Libraries.Describe(X11Libraries.Xss), StringComparison.Ordinal);
        Assert.Contains("libxtst6", X11Libraries.Describe(X11Libraries.Xtst), StringComparison.Ordinal);
    }

    [Fact]
    public void uinput_structures_have_the_sizes_of_uinput_h_and_input_h()
    {
        Assert.Equal(8, Unsafe.SizeOf<InputId>());
        Assert.Equal(92, Unsafe.SizeOf<UinputSetup>()); // 8 + name[80] + 4
        Assert.Equal(UinputProtocol.EventSize, Unsafe.SizeOf<InputEvent>());
        Assert.Equal(24, UinputProtocol.EventSize); // two 8-byte time fields, u16, u16, s32
    }

    [Fact]
    public void ioctl_numbers_match_the_generic_encoding_of_the_uinput_header()
    {
        // _IO('U', 1), _IO('U', 2), _IOW('U', 3, struct uinput_setup), _IOW('U', 100..102, int); the same values
        // that libevdev and the kernel documentation examples produce on x86 and arm64.
        Assert.Equal(0x5501u, UinputProtocol.DevCreate);
        Assert.Equal(0x5502u, UinputProtocol.DevDestroy);
        Assert.Equal(0x405C5503u, UinputProtocol.DevSetup);
        Assert.Equal(0x40045564u, UinputProtocol.SetEvBit);
        Assert.Equal(0x40045565u, UinputProtocol.SetKeyBit);
        Assert.Equal(0x40045566u, UinputProtocol.SetRelBit);
    }

    [Fact]
    public void Event_codes_are_the_ones_of_input_event_codes_h()
    {
        Assert.Equal((ushort)0x00, UinputProtocol.EvSyn);
        Assert.Equal((ushort)0x01, UinputProtocol.EvKey);
        Assert.Equal((ushort)0x02, UinputProtocol.EvRel);
        Assert.Equal((ushort)0x00, UinputProtocol.RelX);
        Assert.Equal((ushort)0x01, UinputProtocol.RelY);
        Assert.Equal((ushort)0x110, UinputProtocol.BtnLeft);
        Assert.Equal((ushort)0x06, UinputProtocol.BusVirtual);
        Assert.Equal(80, UinputProtocol.MaxNameSize);
        Assert.True(UinputProtocol.DeviceName.Length < UinputProtocol.MaxNameSize);
    }

    private static InputEvent[] Events(byte[] bytes)
    {
        Assert.Equal(0, bytes.Length % UinputProtocol.EventSize);
        return [.. Enumerable.Range(0, bytes.Length / UinputProtocol.EventSize)
            .Select(i => MemoryMarshal.Read<InputEvent>(bytes.AsSpan(i * UinputProtocol.EventSize, UinputProtocol.EventSize)))];
    }

    [Fact]
    public void The_mouse_nudge_moves_out_and_back_with_a_report_after_each_move()
    {
        Assert.SkipUnless(nint.Size == 8, "input_event has 8-byte time fields on 64-bit only.");

        var events = Events(UinputProtocol.MouseNudge());

        Assert.Collection(
            events,
            e => Assert.Equal((UinputProtocol.EvRel, UinputProtocol.RelX, 1), (e.Type, e.Code, e.Value)),
            e => Assert.Equal((UinputProtocol.EvSyn, UinputProtocol.SynReport, 0), (e.Type, e.Code, e.Value)),
            e => Assert.Equal((UinputProtocol.EvRel, UinputProtocol.RelX, -1), (e.Type, e.Code, e.Value)),
            e => Assert.Equal((UinputProtocol.EvSyn, UinputProtocol.SynReport, 0), (e.Type, e.Code, e.Value)));
        Assert.Equal(0, events.Where(e => e.Type == UinputProtocol.EvRel).Sum(e => e.Value)); // zero net movement
        Assert.DoesNotContain(events, e => e.Type == UinputProtocol.EvRel && e.Value == 0); // a zero move would be dropped by the kernel
    }

    [Fact]
    public void The_F15_event_presses_and_releases_because_the_kernel_drops_a_release_alone()
    {
        Assert.SkipUnless(nint.Size == 8, "input_event has 8-byte time fields on 64-bit only.");

        var events = Events(UinputProtocol.F15Tap());

        Assert.Collection(
            events,
            e => Assert.Equal((UinputProtocol.EvKey, UinputProtocol.KeyF15, 1), (e.Type, e.Code, e.Value)),
            e => Assert.Equal((UinputProtocol.EvSyn, UinputProtocol.SynReport, 0), (e.Type, e.Code, e.Value)),
            e => Assert.Equal((UinputProtocol.EvKey, UinputProtocol.KeyF15, 0), (e.Type, e.Code, e.Value)),
            e => Assert.Equal((UinputProtocol.EvSyn, UinputProtocol.SynReport, 0), (e.Type, e.Code, e.Value)));
    }
}
