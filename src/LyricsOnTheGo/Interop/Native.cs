using System;
using System.Runtime.InteropServices;
using WinRT;

namespace LyricsOnTheGo.Interop;

/// <summary>
/// Win32 plumbing for the borderless glass backdrop window. It sits directly behind the
/// transparent WPF UI window and only renders the persistent Composition acrylic — it is
/// click-through and never activates, so all input goes to the WPF window on top.
/// </summary>
internal static class Native
{
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TRANSPARENT = 0x00000020;       // click-through
    private const uint WS_EX_TOOLWINDOW = 0x00000080;        // no taskbar button
    private const uint WS_EX_NOACTIVATE = 0x08000000;        // never steals focus
    private const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    private const int SW_SHOWNA = 8;                         // show without activating
    private const int SW_HIDE = 0;
    private const int IDC_ARROW = 32512;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE = 0x0010;

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOREDRAW = 0x0008;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const int WS_EX_LAYERED = 0x00080000;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // Held in a static field so the GC never collects the thunk while Win32 holds it.
    private static WndProcDelegate? _wndProc;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int X, int Y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int left, top, right, bottom; }

    // LOCAL CHANGE (2026-09-29): work area of the monitor a window sits on, so the overlay can be
    // kept out of the taskbar band. Physical pixels, like GetWindowRect (the app is per-monitor aware).
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>Work area (monitor minus the taskbar / other appbars) of the monitor owning hwnd.</summary>
    public static bool TryGetWorkArea(IntPtr hwnd, out RECT work)
    {
        work = new RECT();
        if (hwnd == IntPtr.Zero) return false;
        IntPtr mon = MonitorFromWindow(hwnd, 0x00000002 /* MONITOR_DEFAULTTONEAREST */);
        if (mon == IntPtr.Zero) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return false;
        work = mi.rcWork;
        return work.right > work.left && work.bottom > work.top;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // LOCAL CHANGE (2026-09-29): cap how tall the overlay may be dragged. WM_GETMINMAXINFO is the
    // native way to do it - the struct arrives in the message's lParam (there is NO user32 function
    // called GetMinMaxInfo; an earlier attempt to P/Invoke one crashed the app with
    // EntryPointNotFoundException, see memory 2026-09-29).
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }



    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>Toggles WS_EX_TRANSPARENT on the (already layered) WPF window so it passes clicks through.</summary>
    public static void SetClickThrough(IntPtr hwnd, bool on)
    {
        if (hwnd == IntPtr.Zero)
            return;
        int ex = GetWindowLongW(hwnd, GWL_EXSTYLE);
        ex = on ? ex | (int)WS_EX_TRANSPARENT : ex & ~(int)WS_EX_TRANSPARENT;
        SetWindowLongW(hwnd, GWL_EXSTYLE, ex);
        // Force the new ex-style to take effect immediately (some style changes are otherwise
        // only applied on the next frame change).
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    // --- Host-backdrop opt-in ----------------------------------------------------
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_HOSTBACKDROP = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData
    {
        public int Attrib;
        public IntPtr pvData;
        public int cbData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);

    private static void EnableHostBackdrop(IntPtr hwnd)
    {
        var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_HOSTBACKDROP };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr accentPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WindowCompositionAttribData { Attrib = WCA_ACCENT_POLICY, pvData = accentPtr, cbData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    // --- System DispatcherQueue (required for the Composition compositor) ---------
    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr instance);

    public static Windows.System.DispatcherQueueController? EnsureSystemDispatcherQueueOnCurrentThread()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null)
            return null;

        var options = new DispatcherQueueOptions
        {
            dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
            threadType = 2,     // DQTYPE_THREAD_CURRENT
            apartmentType = 2,  // DQTAT_COM_STA
        };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out IntPtr ptr));
        return MarshalInspectable<Windows.System.DispatcherQueueController>.FromAbi(ptr);
    }

    // --- Window lifecycle --------------------------------------------------------
    public static IntPtr CreateGlassWindow(string className, int x, int y, int width, int height)
    {
        IntPtr hInstance = GetModuleHandleW(null);
        _wndProc = StaticWndProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
            hbrBackground = IntPtr.Zero,
            lpszClassName = Marshal.StringToHGlobalUni(className),
        };
        RegisterClassExW(ref wc);

        // WS_EX_LAYERED | WS_EX_TRANSPARENT is the only combination that makes a window truly
        // transparent to the mouse ACROSS PROCESSES (WS_EX_TRANSPARENT alone absorbs the click on
        // a non-layered window; HTTRANSPARENT only forwards within the same thread). The glass
        // lives on its own thread, so the layered+transparent pair is required for click-through
        // to reach apps behind the overlay.
        uint exStyle = WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOREDIRECTIONBITMAP
                     | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT;
        IntPtr hwnd = CreateWindowExW(exStyle, className, "LyricsOnTheGo", WS_POPUP,
            x, y, width, height, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (hwnd != IntPtr.Zero)
        {
            // A layered window stays invisible until its alpha is set; 255 = fully opaque so the
            // composition acrylic shows at full strength.
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            EnableHostBackdrop(hwnd);
        }

        return hwnd;
    }

    public static void Show(IntPtr hwnd) => ShowWindow(hwnd, SW_SHOWNA);

    /// <summary>Moves + resizes a window in physical pixels (used for karaoke borderless fullscreen).</summary>
    // LOCAL CHANGE (2026-09-29): HWND_TOPMOST = -1. Re-asserting the topmost z-order is the only way
    // to stay above the taskbar, which is itself a topmost window.
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;   // SWP_NOSIZE/NOMOVE/NOACTIVATE already declared above

    // LOCAL CHANGE (2026-09-29): a pure z-order change made by someone else (e.g. the shell
    // raising the taskbar) sends us NO message, so the window hook cannot see it. Ask the z-order
    // directly instead - it is a handful of GetWindow calls, cheap enough to do a few times a second.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GW_HWNDPREV = 0x0003;

    /// <summary>True when nothing in the few windows above us is the taskbar, i.e. we are on top of it.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegisterWindowMessage(string? lpString);

    // LOCAL CHANGE (2026-09-29): cache the taskbar handle - the watchdog calls this ~30 times a
    // second and the handle only changes when Explorer restarts (we re-read it on TaskbarCreated).
    private static IntPtr _taskbar = IntPtr.Zero;

    public static void RefreshTaskbarHandle() => _taskbar = FindWindow("Shell_TrayWnd", null);

    public static bool IsAboveTaskbar(IntPtr hwnd, int maxSteps = 8)
    {
        if (hwnd == IntPtr.Zero) return true;
        if (_taskbar == IntPtr.Zero) RefreshTaskbarHandle();
        if (_taskbar == IntPtr.Zero) return true;
        IntPtr h = hwnd;
        for (int i = 0; i < maxSteps && h != IntPtr.Zero; i++)
        {
            h = GetWindow(h, GW_HWNDPREV);
            if (h == _taskbar) return false;
        }
        return true;
    }

    // LOCAL CHANGE (2026-09-30). The taskbar was not the only thing burying us: clicking any app
    // raises that app (and usually the taskbar with it) into the topmost band, above this overlay.
    // Ask the real question instead - "is a visible window from another process above me?".
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    // Helper/IME windows live in the topmost band above us all the time. They must NOT count as
    // "covering", or the watchdog would push us above the IME candidate window (breaking Chinese
    // input) and burn CPU re-raising for windows that paint nothing.
    private static readonly string[] HelperClasses =
    {
        "IME", "MSCTFIME UI", "Default IME", "ForegroundStaging", "ThumbnailDeviceHelperWnd",
        "ShellHandwritingCanvas", "GDI+ Hook Window Class", "Cascadia Host Window",
        "tooltips_class32", "TaskListThumbnailWnd", "NotifyIconOverflowWindow",
        "Xaml_WindowedPopupClass", "PopupWindow", "Windows.UI.Core.CoreWindow",
    };

    public static bool IsCovered(IntPtr hwnd, uint ownProcessId, int maxSteps = 12)
    {
        if (hwnd == IntPtr.Zero) return false;
        IntPtr h = hwnd;
        var sb = new System.Text.StringBuilder(64);
        for (int i = 0; i < maxSteps; i++)
        {
            h = GetWindow(h, GW_HWNDPREV);          // next window above us in the z-order
            if (h == IntPtr.Zero) return false;      // we are the top of the band
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == ownProcessId) continue;      // our own settings/tray windows may sit above us
            if (!IsWindowVisible(h)) continue;      // hidden windows do not paint anything
            sb.Length = 0;
            GetClassName(h, sb, sb.Capacity);
            string cls = sb.ToString();
            bool helper = false;
            foreach (var hc in HelperClasses)
                if (string.Equals(cls, hc, StringComparison.OrdinalIgnoreCase)) { helper = true; break; }
            if (helper) continue;
            return true;
        }
        return false;
    }

    public static void BringToTop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public static void SetBounds(IntPtr hwnd, int x, int y, int w, int h)
    {
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Shows (without activating) or hides a window — used to hide the glass with the UI.</summary>
    public static void SetVisible(IntPtr hwnd, bool visible)
    {
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, visible ? SW_SHOWNA : SW_HIDE);
    }


    /// <summary>Places the glass window at the given screen rect, directly BEHIND <paramref name="aboveHwnd"/>.</summary>
    public static void PositionBehind(IntPtr hwnd, IntPtr aboveHwnd, int x, int y, int width, int height)
        => SetWindowPos(hwnd, aboveHwnd, x, y, width, height, SWP_NOACTIVATE);

    /// <summary>Thread-safe close request (posts WM_CLOSE to the glass window's own thread).</summary>
    public static void PostClose(IntPtr hwnd) => PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static void RunMessageLoop()
    {
        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }
}
