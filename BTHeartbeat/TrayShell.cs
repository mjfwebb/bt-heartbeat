using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BTHeartbeat.Interop;
using static BTHeartbeat.Interop.NativeMethods;

namespace BTHeartbeat;

/// <summary>
/// The whole UI: a hidden top-level window, a tray icon, a popup menu and the
/// message loop that drives them. This is what WinForms was providing (Application.Run,
/// NotifyIcon, ContextMenuStrip, Timer) minus the ~80MB WindowsDesktop runtime pack,
/// and unlike WinForms it is Native AOT compatible.
///
/// The window is deliberately a normal top-level window that is never shown, not a
/// message-only (HWND_MESSAGE) window: message-only windows cannot be made foreground,
/// and TrackPopupMenu needs a foreground owner or the menu refuses to dismiss when the
/// user clicks away from it.
/// </summary>
public sealed unsafe class TrayShell : ITimerScheduler, IDisposable
{
    private const string WindowClassName = "BTHeartbeatTrayShell";
    private const uint TrayCallbackMessage = WM_APP + 1;
    private const uint StatusChangedMessage = WM_APP + 2;

    private const uint IdmStatus = 1;
    private const uint IdmStartup = 2;
    private const uint IdmExit = 3;

    // One shell per process, so the wndproc thunk can find its instance in a static
    // field. [UnmanagedCallersOnly] methods must be static and cannot capture state.
    private static TrayShell? s_instance;

    private readonly nint _hInstance;
    private readonly nint _hwnd;
    private readonly nint _icon;
    private readonly ushort _classAtom;
    private readonly uint _taskbarCreatedMessage;
    private readonly Dictionary<nuint, Action> _timers = [];
    private readonly string _title;

    private nuint _nextTimerId = 1;
    private string _status = "starting...";
    private bool _disposed;

    /// <summary>Queried each time the menu opens, to render the checkmark.</summary>
    public Func<bool>? IsStartupChecked { get; init; }

    /// <summary>Invoked with the new state when "Start with Windows" is clicked.</summary>
    public Action<bool>? OnStartupToggled { get; init; }

    public TrayShell(string title)
    {
        if (s_instance != null) throw new InvalidOperationException("Only one TrayShell per process.");
        s_instance = this;

        _title = title;
        _hInstance = GetModuleHandleW(0);
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");

        fixed (char* className = WindowClassName)
        {
            var wc = new WNDCLASSW
            {
                lpfnWndProc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint>)&WndProcThunk,
                hInstance = _hInstance,
                lpszClassName = (nint)className,
            };
            _classAtom = RegisterClassW(&wc);
            if (_classAtom == 0) throw new InvalidOperationException($"RegisterClassW failed: {Marshal.GetLastPInvokeError()}");
        }

        _hwnd = CreateWindowExW(0, WindowClassName, title, WS_OVERLAPPED, 0, 0, 0, 0, 0, 0, _hInstance, 0);
        if (_hwnd == 0) throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastPInvokeError()}");

        // Shared system icon: not ours to destroy.
        _icon = LoadIconW(0, IDI_INFORMATION);

        AddOrModifyIcon(NIM_ADD);
    }

    /// <summary>
    /// Updates the tray tooltip. Safe to call from any thread: it hands the string to
    /// the message loop rather than touching the icon directly, which is what the
    /// WindowsFormsSynchronizationContext.Post in the old Program.Main was doing.
    /// </summary>
    public void SetStatus(string status)
    {
        Volatile.Write(ref _status, status);
        PostMessageW(_hwnd, StatusChangedMessage, 0, 0);
    }

    public IDisposable Schedule(int intervalMs, Action onTick)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        nuint id = _nextTimerId++;
        _timers[id] = onTick;
        if (SetTimer(_hwnd, id, (uint)intervalMs, 0) == 0)
        {
            _timers.Remove(id);
            throw new InvalidOperationException($"SetTimer failed: {Marshal.GetLastPInvokeError()}");
        }
        return new TimerHandle(this, id);
    }

    /// <summary>Pumps messages until the tray menu's Exit posts WM_QUIT.</summary>
    public void Run()
    {
        MSG msg;
        while (true)
        {
            int result = GetMessageW(&msg, 0, 0, 0);
            if (result is 0 or -1) return; // 0 = WM_QUIT, -1 = error
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
    }

    // ---- message handling

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WndProcThunk(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        // An exception must never unwind into the Win32 dispatcher.
        try
        {
            var self = s_instance;
            if (self != null) return self.WndProc(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[BTHeartbeat] window proc failed: {ex}");
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_TIMER)
        {
            if (_timers.TryGetValue((nuint)wParam, out var onTick)) onTick();
            return 0;
        }

        if (msg == StatusChangedMessage)
        {
            AddOrModifyIcon(NIM_MODIFY);
            return 0;
        }

        if (msg == TrayCallbackMessage)
        {
            // Legacy (pre-NOTIFYICON_VERSION_4) callback shape: lParam is the mouse message.
            if ((uint)lParam is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
            return 0;
        }

        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer restarted and dropped every tray icon; put ours back.
            AddOrModifyIcon(NIM_ADD);
            return 0;
        }

        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            bool startupChecked = IsStartupChecked?.Invoke() ?? false;

            AppendMenuW(menu, MF_STRING | MF_GRAYED, IdmStatus, $"Status: {Volatile.Read(ref _status)}");
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING | (startupChecked ? MF_CHECKED : 0), IdmStartup, "Start with Windows");
            AppendMenuW(menu, MF_STRING, IdmExit, "Exit");

            POINT cursor;
            if (!GetCursorPos(&cursor)) return;

            // Foreground first, then the documented WM_NULL poke afterwards: without the
            // pair the menu sticks around after the user clicks elsewhere.
            SetForegroundWindow(_hwnd);
            int command = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, cursor.X, cursor.Y, _hwnd, 0);
            PostMessageW(_hwnd, WM_NULL, 0, 0);

            switch ((uint)command)
            {
                case IdmStartup:
                    OnStartupToggled?.Invoke(!startupChecked);
                    break;
                case IdmExit:
                    PostQuitMessage(0);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    // ---- tray icon

    private void AddOrModifyIcon(uint message)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _icon,
        };

        string tip = $"{_title}: {Volatile.Read(ref _status)}";
        int length = Math.Min(tip.Length, TipCapacity - 1);
        tip.AsSpan(0, length).CopyTo(new Span<char>(data.szTip, TipCapacity - 1));
        data.szTip[length] = '\0';

        Shell_NotifyIconW(message, &data);
    }

    // ---- teardown

    private sealed class TimerHandle(TrayShell shell, nuint id) : IDisposable
    {
        private bool _stopped;

        public void Dispose()
        {
            if (_stopped) return;
            _stopped = true;
            KillTimer(shell._hwnd, id);
            shell._timers.Remove(id);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (nuint id in _timers.Keys) KillTimer(_hwnd, id);
        _timers.Clear();

        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = 1,
        };
        Shell_NotifyIconW(NIM_DELETE, &data);

        if (_hwnd != 0) DestroyWindow(_hwnd);
        if (_classAtom != 0)
        {
            fixed (char* className = WindowClassName) UnregisterClassW(className, _hInstance);
        }

        s_instance = null;
    }
}
