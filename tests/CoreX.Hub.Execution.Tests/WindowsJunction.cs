using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CoreX.Hub.Execution.Tests;

/// <summary>
/// Creates a real NTFS directory junction so the reparse point escape test exercises the
/// filesystem instead of a mocked FileAttributes flag.
///
/// A junction is used rather than a symbolic link because directory symlinks require
/// administrator rights on Windows, and a privilege dependent test would silently skip
/// exactly the case that matters. Test infrastructure only.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsJunction
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;

    /// <summary>Creates <paramref name="junctionPath"/> pointing at <paramref name="targetPath"/>.</summary>
    public static void Create(string junctionPath, string targetPath)
    {
        Directory.CreateDirectory(junctionPath);

        var printName = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar);
        var substituteName = "\\??\\" + printName;

        var substituteBytes = Encoding.Unicode.GetBytes(substituteName);
        var printBytes = Encoding.Unicode.GetBytes(printName);

        // REPARSE_DATA_BUFFER for IO_REPARSE_TAG_MOUNT_POINT.
        var pathBufferLength = substituteBytes.Length + 2 + printBytes.Length + 2;
        var reparseDataLength = 8 + pathBufferLength;
        var buffer = new byte[16 + pathBufferLength];

        BitConverter.TryWriteBytes(buffer.AsSpan(0), IoReparseTagMountPoint);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), (ushort)reparseDataLength);
        BitConverter.TryWriteBytes(buffer.AsSpan(6), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), (ushort)0);
        BitConverter.TryWriteBytes(buffer.AsSpan(10), (ushort)substituteBytes.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(12), (ushort)(substituteBytes.Length + 2));
        BitConverter.TryWriteBytes(buffer.AsSpan(14), (ushort)printBytes.Length);

        substituteBytes.CopyTo(buffer.AsSpan(16));
        printBytes.CopyTo(buffer.AsSpan(16 + substituteBytes.Length + 2));

        var raw = CreateFileW(
            junctionPath,
            GenericWrite,
            FileShareReadWriteDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        using var handle = new SafeFileHandle(raw, ownsHandle: true);
        if (handle.IsInvalid)
        {
            throw new IOException(
                $"CreateFile on '{junctionPath}' failed with Win32 error {Marshal.GetLastWin32Error()}.");
        }

        unsafe
        {
            fixed (byte* pinned = buffer)
            {
                if (!DeviceIoControl(
                        handle,
                        FsctlSetReparsePoint,
                        pinned,
                        (uint)buffer.Length,
                        null,
                        0,
                        out _,
                        IntPtr.Zero))
                {
                    throw new IOException(
                        $"FSCTL_SET_REPARSE_POINT on '{junctionPath}' failed with Win32 error {Marshal.GetLastWin32Error()}.");
                }
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        void* inBuffer,
        uint inBufferSize,
        void* outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
