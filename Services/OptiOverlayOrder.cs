using System.Runtime.InteropServices;
using System.Text;

namespace GameLocker.Services;

/// <summary>Keeps a GameLocker window above its capture output without racing its topmost timer.</summary>
internal sealed class OptiOverlayOrder
{
    private const string OverlayClass = "OptiCaptureHostOverlay";
    private const uint GW_OWNER = 4;
    private const int GWLP_HWNDPARENT = -8;
    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;
    private const uint RaiseFlags = 0x0001 | 0x0002 | 0x0010 | 0x0400; // NOSIZE | NOMOVE | NOACTIVATE | NOSENDCHANGING
    private nint _attachedOwner;

    /// <summary>Called on the UI thread when showing the window and while it stays visible.</summary>
    internal bool Update(nint window, int hostPid)
    {
        if (window == 0 || (hostPid <= 0 && _attachedOwner == 0)) return false;
        var owner = GetWindow(window, GW_OWNER);
        if (_attachedOwner != 0 && owner == _attachedOwner)
        {
            if (IsVisibleOutput(owner, hostPid)) return true;
            Detach(window);
            owner = GetWindow(window, GW_OWNER);
        }
        else _attachedOwner = 0;

        // Leave preexisting ownership intact. GWLP_HWNDPARENT is an owner only on a top-level window.
        if (hostPid <= 0 || owner != 0 || (GetWindowLongPtrW(window, GWL_STYLE).ToInt64() & WS_CHILD) != 0)
            return false;
        nint output = 0;
        EnumWindows((candidate, _) =>
        {
            if (!IsVisibleOutput(candidate, hostPid)) return true;
            output = candidate;
            return false;
        }, 0);
        if (output == 0) return false;

        // An owned window stays above its owner even when the latter reasserts HWND_TOPMOST.
        SetWindowLongPtrW(window, GWLP_HWNDPARENT, output);
        if (GetWindow(window, GW_OWNER) != output) return false;
        _attachedOwner = output;
        SetWindowPos(window, -1, 0, 0, 0, 0, RaiseFlags);
        return true;
    }

    internal void Detach(nint window)
    {
        if (_attachedOwner == 0) return;
        if (window != 0 && GetWindow(window, GW_OWNER) == _attachedOwner)
        {
            SetWindowLongPtrW(window, GWLP_HWNDPARENT, 0);
            if (GetWindow(window, GW_OWNER) == _attachedOwner) return;
        }
        _attachedOwner = 0;
    }

    private static bool IsVisibleOutput(nint window, int hostPid)
    {
        if (hostPid <= 0 || GetWindowThreadProcessId(window, out var pid) == 0 || pid != (uint)hostPid
            || !IsWindowVisible(window) || IsIconic(window)) return false;
        var name = new StringBuilder(64);
        return GetClassNameW(window, name, name.Capacity) != 0 && name.ToString() == OverlayClass;
    }

    private delegate bool EnumWindowsProc(nint window, nint state);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint state);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool IsIconic(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassNameW(nint window, StringBuilder name, int capacity);
}
