using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Vigil.Platform.Windows;

/// <summary>
/// Replaces a file by renaming a finished file over it with POSIX semantics, so the replace succeeds while other programs have the
/// destination open.
/// </summary>
/// <remarks>
/// <c>File.Move(source, destination, overwrite: true)</c> calls <c>MoveFileExW</c> with MOVEFILE_COPY_ALLOWED | MOVEFILE_REPLACE_EXISTING
/// (dotnet/runtime, <c>Interop.MoveFileEx.cs</c>). That is the classic rename, which cannot replace a destination that is open, even when
/// the reader shares FILE_SHARE_DELETE (measured on windows-2025: the tray's save failed while the daemon's reader polled).
/// The documented way around it is the rename flag FILE_RENAME_FLAG_POSIX_SEMANTICS (FILE_RENAME_POSIX_SEMANTICS in the driver docs): "If
/// FILE_RENAME_REPLACE_IF_EXISTS is also specified, allow replacing a file even if there are existing handles to it. Existing handles to
/// the replaced file continue to be valid for operations such as read and write. Any subsequent opens of the target name will open the
/// renamed file, not the replaced file."
/// https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information
/// It is requested through <c>SetFileInformationByHandle</c> with <c>FileRenameInfoEx</c>
/// (https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-setfileinformationbyhandle,
/// https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_rename_info, whose Flags field is used for that class).
/// The Learn pages do not give the numeric values; they are those of the Windows SDK as mirrored in the MinGW-w64 headers
/// (minwinbase.h: FileRenameInfoEx is the 23rd member of FILE_INFO_BY_HANDLE_CLASS, value 22; winbase.h: FILE_RENAME_FLAG_REPLACE_IF_EXISTS
/// 0x1, FILE_RENAME_FLAG_POSIX_SEMANTICS 0x2). The caller falls back to the classic rename when this returns false (a file system or
/// Windows build that does not support the request), so nothing gets worse than before.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class AtomicFileReplace
{
    private const uint Delete = 0x00010000; // DELETE standard access right: needed to rename through a handle.
    private const uint ShareAll = 0x7; // FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const int FileRenameInfoEx = 22;
    private const uint ReplaceIfExists = 0x1;
    private const uint PosixSemantics = 0x2;

    // FILE_RENAME_INFO on 64-bit: DWORD Flags (padded to 8), HANDLE RootDirectory at 8, DWORD FileNameLength at 16, WCHAR FileName at 20.
    // On 32-bit the handle is 4 bytes, so FileName starts at 12.
    private static readonly int FileNameOffset = nint.Size == 8 ? 20 : 12;

    /// <summary>
    /// Renames <paramref name="source"/> over <paramref name="destination"/> (same volume) with POSIX semantics. Returns false, leaving
    /// both files as they were, when the request is refused; <paramref name="error"/> then holds the Win32 error code.
    /// </summary>
    public static unsafe bool TryReplace(string source, string destination, out int error)
    {
        using var handle = NativeMethods.CreateFileW(Path.GetFullPath(source), Delete, ShareAll, 0, OpenExisting, FileAttributeNormal, 0);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        var name = Path.GetFullPath(destination);
        var nameBytes = name.Length * sizeof(char);
        var buffer = new byte[FileNameOffset + nameBytes + sizeof(char)]; // The name is followed by a NUL, which FileNameLength does not count.
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), ReplaceIfExists | PosixSemantics);
        BitConverter.TryWriteBytes(buffer.AsSpan(nint.Size == 8 ? 16 : 8, 4), nameBytes);
        System.Text.Encoding.Unicode.GetBytes(name, buffer.AsSpan(FileNameOffset));

        fixed (byte* pointer = buffer)
        {
            if (NativeMethods.SetFileInformationByHandle(handle, FileRenameInfoEx, pointer, (uint)buffer.Length))
            {
                error = 0;
                return true;
            }
        }

        error = Marshal.GetLastPInvokeError();
        return false;
    }
}
