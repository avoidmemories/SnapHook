using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnapHook.Services;

/// <summary>
/// Native notification-area icon backed directly by Windows Shell_NotifyIcon.
/// This keeps the tray menu and icon rendering in the Windows shell instead of
/// drawing either of them through WPF.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifShowTip = 0x00000080;
    private const uint WmUser = 0x0400;
    private const uint WmTrayIcon = WmUser + 1;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmCommand = 0x0111;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;
    private const uint TpmRightButton = 0x0002;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int MenuToggle = 1;
    private const int MenuExit = 2;

    private readonly Action _onOpen;
    private readonly Action _onToggle;
    private readonly Action _onExit;
    private readonly string _tooltip;
    private readonly HwndSource _messageWindow;
    private readonly IntPtr _windowHandle;
    private readonly IntPtr _activeIcon;
    private readonly IntPtr _inactiveIcon;
    private readonly uint _taskbarCreatedMessage;
    private bool _isActive;
    private bool _added;
    private bool _disposed;

    public TrayIconService(
        string tooltip,
        Action onOpen,
        Action onToggle,
        Action onExit,
        bool isActive)
    {
        _onOpen = onOpen;
        _onToggle = onToggle;
        _onExit = onExit;
        _tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _isActive = isActive;

        // This must be a hidden top-level window, not HWND_MESSAGE. Explorer
        // broadcasts TaskbarCreated after it recreates the notification area,
        // and message-only windows deliberately do not receive broadcasts.
        // WS_EX_TOOLWINDOW keeps this infrastructure window out of Alt+Tab and
        // the taskbar; without WS_VISIBLE it is never drawn on the desktop.
        var parameters = new HwndSourceParameters("SnapHook_TrayWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExToolWindow | WsExNoActivate
        };

        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);
        _windowHandle = _messageWindow.Handle;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        // The active state comes straight from the executable's application icon,
        // exactly as Windows displays it in Explorer and the taskbar.
        _activeIcon = LoadExecutableIcon();
        _inactiveIcon = LoadIconFromResource();

        _added = AddIcon();
    }

    /// <summary>Switches the visual state and the next context-menu verb.</summary>
    public void SetActive(bool active)
    {
        if (_disposed || _isActive == active)
        {
            return;
        }

        _isActive = active;
        if (!_added)
        {
            return;
        }

        var data = CreateData(NifIcon);
        data.hIcon = CurrentIcon;
        _added = ShellNotifyIcon(NimModify, ref data);
    }

    private IntPtr CurrentIcon =>
        _isActive || _inactiveIcon == IntPtr.Zero ? _activeIcon : _inactiveIcon;

    /// <summary>
    /// Registers the complete current tray state. Explorer owns this registration,
    /// so it must be made again after Explorer recreates its notification area.
    /// </summary>
    private bool AddIcon()
    {
        var data = CreateData(NifMessage | NifIcon | NifTip | NifShowTip);
        data.uCallbackMessage = WmTrayIcon;
        data.hIcon = CurrentIcon;
        data.szTip = _tooltip;
        return ShellNotifyIcon(NimAdd, ref data);
    }

    private NOTIFYICONDATA CreateData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _windowHandle,
        uID = 1,
        uFlags = flags
    };

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            // This is a one-shot, event-driven recovery. No timer or polling is
            // involved: Explorer has just told every top-level window that its
            // notification area was replaced, so the old icon registration is gone.
            if (!_disposed)
            {
                _added = AddIcon();
            }

            handled = true;
        }
        else if (message == WmTrayIcon)
        {
            var mouseMessage = (uint)lParam.ToInt64() & 0xFFFF;
            if (mouseMessage == WmLButtonUp)
            {
                _onOpen();
                handled = true;
            }
            else if (mouseMessage == WmRButtonUp)
            {
                ShowContextMenu();
                handled = true;
            }
        }
        else if (message == WmCommand)
        {
            switch (wParam.ToInt32() & 0xFFFF)
            {
                case MenuToggle:
                    _onToggle();
                    handled = true;
                    break;
                case MenuExit:
                    _onExit();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, MenuToggle, _isActive ? "Deactivate" : "Activate");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, MenuExit, "Exit");

            // The foreground hand-off makes Windows dismiss the menu when the user
            // clicks elsewhere, matching the behaviour of ordinary tray icons.
            SetForegroundWindow(_windowHandle);
            GetCursorPos(out var point);
            TrackPopupMenu(menu, TpmRightButton, point.X, point.Y, 0, _windowHandle, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            var data = CreateData(0);
            ShellNotifyIcon(NimDelete, ref data);
            _added = false;
        }

        if (_activeIcon != IntPtr.Zero)
        {
            DestroyIcon(_activeIcon);
        }

        if (_inactiveIcon != IntPtr.Zero)
        {
            DestroyIcon(_inactiveIcon);
        }

        _messageWindow.Dispose();
    }

    private static IntPtr LoadExecutableIcon()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(executablePath)
                ? IntPtr.Zero
                : ExtractIcon(IntPtr.Zero, executablePath, 0);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Creates a Windows icon from the compiled WPF resource. The icon is never
    /// written beside the executable, so the published folder contains no tray-only
    /// asset.
    /// </summary>
    private static IntPtr LoadIconFromResource()
    {
        try
        {
            var resource = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/TrayInactive.ico", UriKind.Absolute));
            if (resource is null)
            {
                return IntPtr.Zero;
            }

            using (resource.Stream)
            {
                var frame = BitmapFrame.Create(
                    resource.Stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                frame.Freeze();
                return CreateIcon(frame);
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static IntPtr CreateIcon(BitmapSource source)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        bitmap.Freeze();

        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return IntPtr.Zero;
        }

        var stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        var info = new BITMAPINFO
        {
            Header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                // A top-down DIB preserves the row order WPF returned.
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0
            }
        };

        var colorBitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (colorBitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var maskBitmap = IntPtr.Zero;
        try
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            maskBitmap = CreateBitmap(width, height, 1, 1, IntPtr.Zero);
            if (maskBitmap == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            var icon = new ICONINFO
            {
                fIcon = true,
                hbmColor = colorBitmap,
                hbmMask = maskBitmap
            };
            return CreateIconIndirect(ref icon);
        }
        finally
        {
            if (maskBitmap != IntPtr.Zero)
            {
                DeleteObject(maskBitmap);
            }

            DeleteObject(colorBitmap);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr instance, string executablePath, uint iconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint itemId, string? text);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr window,
        IntPtr rectangle);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr deviceContext,
        [In] ref BITMAPINFO bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect([In] ref ICONINFO iconInfo);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr objectHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}
