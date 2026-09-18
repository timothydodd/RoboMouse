using System.Runtime.InteropServices;

namespace RoboMouse.Helper;

/// <summary>
/// Keeps the calling thread attached to whichever desktop is receiving input. SendInput and
/// GetCursorPos only act on the calling thread's desktop, so when a UAC prompt or the lock screen
/// switches input to "Winlogon" the inject thread has to follow it, and follow it back afterwards.
/// SetThreadDesktop only works on a thread that owns no windows or hooks, which is why injection
/// runs on its own plain thread.
/// </summary>
internal sealed unsafe partial class InputDesktop : IDisposable
{
    private const uint GENERIC_ALL = 0x10000000;
    private const int UOI_NAME = 2;

    private nint _attached;
    private string _name = string.Empty;

    /// <summary>Name of the desktop this thread is attached to, for logging.</summary>
    public string Name => _name;

    /// <summary>
    /// Attaches to the current input desktop if it is not the one already attached. Returns true when
    /// the thread moved to a different desktop.
    /// </summary>
    public bool Follow()
    {
        var desktop = OpenInputDesktop(0, false, GENERIC_ALL);
        if (desktop == 0)
            return false;

        var name = ReadName(desktop);
        if (_attached != 0 && name == _name)
        {
            CloseDesktop(desktop);
            return false;
        }

        if (!SetThreadDesktop(desktop))
        {
            CloseDesktop(desktop);
            return false;
        }

        if (_attached != 0)
            CloseDesktop(_attached);
        _attached = desktop;
        _name = name;
        return true;
    }

    private static string ReadName(nint desktop)
    {
        var buffer = stackalloc char[256];
        uint needed;
        return GetUserObjectInformationW(desktop, UOI_NAME, buffer, 256 * sizeof(char), &needed)
            ? new string(buffer)
            : string.Empty;
    }

    public void Dispose()
    {
        if (_attached != 0)
            CloseDesktop(_attached);
        _attached = 0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadDesktop(nint hDesktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint hDesktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformationW(nint hObj, int nIndex, char* pvInfo, uint nLength, uint* lpnLengthNeeded);
}
