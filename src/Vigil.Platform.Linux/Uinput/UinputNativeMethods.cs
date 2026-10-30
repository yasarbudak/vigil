using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigil.Platform.Linux.Uinput;

/// <summary>
/// ioctl and write of the C library (glibc; the two targets are linux-x64 and linux-arm64 with glibc, not musl). The file is
/// opened with <c>File.OpenHandle</c>, so only the two calls that .NET has no API for are imported.
/// <c>ioctl</c> is declared <c>int ioctl(int fd, unsigned long request, ...)</c> (ioctl(2)); the three overloads below fix the
/// variadic part to what each request takes. On x86-64 and on arm64 Linux a variadic call with integer or pointer arguments
/// uses the same registers as a fixed one (System V psABI, AAPCS64), and the value argument is passed as a full
/// register-sized integer so that the upper half is not left undefined: uinput takes it as an <c>unsigned long</c>.
/// </summary>
[SupportedOSPlatform("linux")]
internal static unsafe partial class UinputNativeMethods
{
    private const string Libc = "libc.so.6";

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, nuint request);

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, nuint request, nint value);

    [LibraryImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    internal static partial int Ioctl(int fd, nuint request, void* pointer);

    // unistd.h: ssize_t write(int fd, const void *buf, size_t count)
    [LibraryImport(Libc, EntryPoint = "write", SetLastError = true)]
    internal static partial nint Write(int fd, byte* buffer, nuint count);
}
