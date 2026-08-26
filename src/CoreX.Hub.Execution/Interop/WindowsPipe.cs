using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace CoreX.Hub.Execution.Interop;

/// <summary>
/// One anonymous pipe, with the two ends kept deliberately apart.
///
/// A redirected stream is two handles that must end up in different processes, and the whole
/// security question is which of them the child is allowed to have. So they are created
/// inheritable - that is the only way a handle reaches a child at all - and then the end this
/// process keeps is immediately marked non-inheritable again.
///
/// That second step is not decoration. Without it the child would receive a duplicate of the
/// parent's own end: for stdout that means the child holds the read end of its own output, so
/// the pipe never reports end-of-stream when the child exits and the parent waits for a
/// writer that will never close. The classic redirected-pipe hang is an inheritance bug, not
/// a draining bug.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPipe : IDisposable
{
    private const uint HandleFlagInherit = 0x00000001;

    private WindowsPipe(SafeFileHandle read, SafeFileHandle write, bool childReads)
    {
        Read = read;
        Write = write;
        ChildEnd = childReads ? read : write;
        ParentEnd = childReads ? write : read;
    }

    public SafeFileHandle Read { get; }

    public SafeFileHandle Write { get; }

    /// <summary>The end the child inherits. The only handle in the inheritance list.</summary>
    public SafeFileHandle ChildEnd { get; }

    /// <summary>The end this process keeps. Never inheritable.</summary>
    public SafeFileHandle ParentEnd { get; }

    /// <summary>
    /// Creates a pipe for one redirected stream.
    ///
    /// <paramref name="childReads"/> selects which way it points: true for the child's stdin,
    /// false for its stdout and stderr.
    /// </summary>
    public static WindowsPipe Create(bool childReads)
    {
        var attributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            SecurityDescriptor = IntPtr.Zero,

            // Inheritable at birth; the parent's end is revoked below. There is no way to
            // create one inheritable end and one private end in a single call.
            InheritHandle = 1,
        };

        if (!CreatePipe(out var read, out var write, ref attributes, 0))
        {
            var error = Marshal.GetLastWin32Error();
            read?.Dispose();
            write?.Dispose();

            throw new InvalidOperationException($"CreatePipe failed with Win32 error {error}.");
        }

        var pipe = new WindowsPipe(read, write, childReads);

        if (!SetHandleInformation(pipe.ParentEnd, HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastWin32Error();
            pipe.Dispose();

            throw new InvalidOperationException(
                $"SetHandleInformation failed with Win32 error {error}; the parent pipe end would have been inherited.");
        }

        return pipe;
    }

    /// <summary>
    /// Releases the end the child now owns.
    ///
    /// Called once the child exists. Until the parent's duplicate is gone the pipe still has
    /// an extra writer (or reader) and cannot signal end-of-stream.
    /// </summary>
    public void CloseChildEnd() => ChildEnd.Dispose();

    public void Dispose()
    {
        Read.Dispose();
        Write.Dispose();
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes attributes,
        uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);

    /// <summary>
    /// Win32 SECURITY_ATTRIBUTES, kept blittable.
    ///
    /// <c>bInheritHandle</c> is a Win32 BOOL - a four byte integer - and is declared as one
    /// here rather than as a managed bool. A managed bool would need runtime marshalling,
    /// which the source generated interop layer refuses for a struct passed by reference.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }
}
