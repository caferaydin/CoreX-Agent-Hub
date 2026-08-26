using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace CoreX.Hub.Execution.Interop;

/// <summary>
/// The primary thread of a suspended child, owned so it cannot be leaked or closed twice.
///
/// <c>CreateProcessW</c> hands back two handles and the caller owns both. The process handle
/// has a framework type; the thread handle does not, and it is the one that is easy to drop -
/// it is needed for exactly one call (<c>ResumeThread</c>) and is dead weight afterwards, so
/// a raw IntPtr version of it tends to survive the happy path and leak on every other one.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class SafeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeThreadHandle(IntPtr existingHandle) : base(ownsHandle: true) => SetHandle(existingHandle);

    protected override bool ReleaseHandle() => CloseHandle(handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
