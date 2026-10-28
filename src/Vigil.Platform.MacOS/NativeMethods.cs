using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigil.Platform.MacOS;

/// <summary>CGPoint (CGGeometry.h). CGFloat is double on 64-bit (CGBase.h, CGFLOAT_TYPE).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct CGPoint(double x, double y)
{
    public readonly double X = x;
    public readonly double Y = y;
}

/// <summary>
/// Every import and constant below was checked against the macOS SDK headers
/// (xcrun --show-sdk-path) named in the adjacent comment. Only posting and idle reading are used;
/// no event taps or listen APIs.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class NativeMethods
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";

    // CGEventTypes.h: CGEventSourceStateID, kCGEventSourceStateHIDSystemState = 1
    internal const int EventSourceStateHidSystemState = 1;

    // CGEventTypes.h: #define kCGAnyInputEventType ((CGEventType)(~0)), CGEventType is uint32_t
    internal const uint AnyInputEventType = uint.MaxValue;

    // CGEventTypes.h: kCGEventMouseMoved = NX_MOUSEMOVED; IOKit hidsystem/IOLLEvent.h: NX_MOUSEMOVED = 5
    internal const uint EventMouseMoved = 5;

    // CGEventTypes.h: CGMouseButton, kCGMouseButtonLeft = 0
    internal const uint MouseButtonLeft = 0;

    // CGEventTypes.h: CGEventTapLocation, kCGHIDEventTap = 0
    internal const uint HidEventTap = 0;

    // Carbon HIToolbox Events.h: kVK_F15 = 0x71
    internal const ushort KeyCodeF15 = 0x71;

    // CFString.h: kCFStringEncodingUTF8 = 0x08000100
    internal const uint StringEncodingUtf8 = 0x08000100;

    // CFBase.h: void CFRelease(CFTypeRef cf)
    [LibraryImport(CoreFoundation)]
    internal static partial void CFRelease(nint cf);

    // CFString.h: Boolean CFStringGetCString(CFStringRef, char *buffer, CFIndex bufferSize, CFStringEncoding)
    // Boolean is unsigned char (CFBase.h); CFIndex is signed long (CFBase.h).
    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static unsafe partial bool CFStringGetCString(nint theString, byte* buffer, nint bufferSize, uint encoding);

    // CFString.h: CFStringRef CFStringCreateWithCString(CFAllocatorRef, const char *cStr, CFStringEncoding) (Create rule: caller releases)
    // CFBase.h: a NULL allocator means "use the default" (same as kCFAllocatorDefault).
    [LibraryImport(CoreFoundation)]
    internal static unsafe partial CFTypeHandle CFStringCreateWithCString(nint allocator, byte* cStr, uint encoding);

    // CFDictionary.h: Boolean CFDictionaryGetValueIfPresent(CFDictionaryRef, const void *key, const void **value)
    // The returned value follows the "Get" rule (owned by the dictionary, not released).
    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool CFDictionaryGetValueIfPresent(CFTypeHandle dictionary, CFTypeHandle key, out nint value);

    // CFBase.h: CFTypeID CFGetTypeID(CFTypeRef cf); CFTypeID is unsigned long (CFBase.h, non-LLP64)
    [LibraryImport(CoreFoundation)]
    internal static partial nuint CFGetTypeID(nint cf);

    // CFNumber.h: CFTypeID CFBooleanGetTypeID(void)
    [LibraryImport(CoreFoundation)]
    internal static partial nuint CFBooleanGetTypeID();

    // CFNumber.h: Boolean CFBooleanGetValue(CFBooleanRef)
    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool CFBooleanGetValue(nint boolean);

    // CGSession.h: CFDictionaryRef CGSessionCopyCurrentDictionary(void) (Copy rule: caller releases; NULL outside a GUI session)
    [LibraryImport(CoreGraphics)]
    internal static partial CFTypeHandle CGSessionCopyCurrentDictionary();

    // CGEventSource.h: CFTimeInterval CGEventSourceSecondsSinceLastEventType(CGEventSourceStateID, CGEventType)
    [LibraryImport(CoreGraphics)]
    internal static partial double CGEventSourceSecondsSinceLastEventType(int stateId, uint eventType);

    // CGEventSource.h: CGEventSourceRef CGEventSourceCreate(CGEventSourceStateID) (Create rule: caller releases)
    [LibraryImport(CoreGraphics)]
    internal static partial CFTypeHandle CGEventSourceCreate(int stateId);

    // CGEvent.h: CGEventRef CGEventCreate(CGEventSourceRef) (Create rule: caller releases)
    [LibraryImport(CoreGraphics)]
    internal static partial CFTypeHandle CGEventCreate(nint source);

    // CGEvent.h: CGPoint CGEventGetLocation(CGEventRef)
    [LibraryImport(CoreGraphics)]
    internal static partial CGPoint CGEventGetLocation(CFTypeHandle @event);

    // CGEvent.h: CGEventRef CGEventCreateMouseEvent(CGEventSourceRef, CGEventType, CGPoint, CGMouseButton)
    [LibraryImport(CoreGraphics)]
    internal static partial CFTypeHandle CGEventCreateMouseEvent(
        CFTypeHandle source, uint mouseType, CGPoint position, uint mouseButton);

    // CGEvent.h: CGEventRef CGEventCreateKeyboardEvent(CGEventSourceRef, CGKeyCode, bool keyDown)
    // CGKeyCode is uint16_t (CGRemoteOperation.h); C99 bool is one byte.
    [LibraryImport(CoreGraphics)]
    internal static partial CFTypeHandle CGEventCreateKeyboardEvent(
        CFTypeHandle source, ushort virtualKey, [MarshalAs(UnmanagedType.U1)] bool keyDown);

    // CGEvent.h: void CGEventPost(CGEventTapLocation, CGEventRef)
    [LibraryImport(CoreGraphics)]
    internal static partial void CGEventPost(uint tap, CFTypeHandle @event);

    // CGEvent.h: bool CGPreflightPostEventAccess(void) (does not prompt)
    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool CGPreflightPostEventAccess();

    // CGEvent.h: bool CGRequestPostEventAccess(void) (may prompt; only called by "vigil permissions request")
    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool CGRequestPostEventAccess();

    // IOPowerSources.h: CFTypeRef IOPSCopyPowerSourcesInfo(void) (Copy rule: caller releases)
    [LibraryImport(IOKit)]
    internal static partial CFTypeHandle IOPSCopyPowerSourcesInfo();

    // IOPowerSources.h: CFStringRef IOPSGetProvidingPowerSourceType(CFTypeRef snapshot) (Get rule: not released)
    [LibraryImport(IOKit)]
    internal static partial nint IOPSGetProvidingPowerSourceType(CFTypeHandle snapshot);
}
