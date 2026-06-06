using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ResizeWidth
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<WindowItem> _windows = new();
        private readonly ObservableCollection<MonitorItem> _monitors = new();
        // Ignore rules: each is ("exact", value) or ("contains", value)
        private readonly List<(string mode, string pattern)> _ignoreRules = new();

        private static readonly string _ignoreFilePath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath)!, "ignored_processes.txt");

        private static readonly string _arrangementFilePath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath)!, "monitor_arrangements.txt");

        private static readonly string _lastResizeFilePath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath)!, "last_resize.txt");

        // Key = process name (case-insensitive); Value = (extendRight, percent)
        private readonly Dictionary<string, (bool extendRight, int percent)> _lastResizeActions =
            new(StringComparer.OrdinalIgnoreCase);

        // Key = sorted comma-joined "model-UIDxxx" strings, Value = list of (id, x, y, w, h)
        private readonly Dictionary<string, List<(string id, int x, int y, uint w, uint h)>> _arrangements = new();

        private const int HotkeyId_MaxRight    = 1;
        private const int HotkeyId_MaxLeft      = 2;
        private const int HotkeyId_RestoreArr   = 3;
        private HwndSource? _hwndSource;
        private IntPtr _hWnd;
        private IntPtr _previousForegroundHwnd;
        private IntPtr _winEventHook;
        private NativeMethods.WinEventDelegate? _winEventDelegate;
        private IntPtr _kbHook;
        private NativeMethods.LowLevelKeyboardProc? _kbHookProc;
        private bool _winKeyDown;

        // Caches to avoid expensive Win32/process calls on every refresh
        private readonly Dictionary<IntPtr, string> _processNameCache = new();
        private readonly Dictionary<IntPtr, BitmapSource?> _iconCache = new();

        public MainWindow()
        {
            InitializeComponent();

            var buildTimestampStr = Assembly.GetExecutingAssembly()
                .GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildTimestamp")?.Value;
            if (DateTime.TryParse(buildTimestampStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var buildUtc))
            {
                var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                var buildEastern = TimeZoneInfo.ConvertTimeFromUtc(buildUtc, eastern);
                bool isDaylight = eastern.IsDaylightSavingTime(buildEastern);
                Title = $"ResizeWidth — Built {buildEastern:yyyy-MM-dd h:mm tt} {(isDaylight ? "EDT" : "EST")}";
            }
            else
            {
                Title = "ResizeWidth";
            }

            WindowListBox.ItemsSource = _windows;
            MonitorListBox.ItemsSource = _monitors;
            LoadArrangementFile();
            LoadLastResizeAction();
            LoadIgnoreFile();
            SourceInitialized += MainWindow_SourceInitialized;
            Closed += MainWindow_Closed;
        }



        private void LoadArrangementFile()
        {
            try
            {
                if (!File.Exists(_arrangementFilePath)) return;
                var lines = File.ReadAllLines(_arrangementFilePath);
                int i = 0;
                while (i < lines.Length)
                {
                    // Skip blank lines
                    if (string.IsNullOrWhiteSpace(lines[i])) { i++; continue; }

                    string setKey = lines[i].Trim();
                    i++;
                    var entries = new List<(string model, int x, int y, uint w, uint h)>();
                    while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                    {
                        var parts = lines[i].Split('\t');
                        if (parts.Length == 5
                            && int.TryParse(parts[1], out int x)
                            && int.TryParse(parts[2], out int y)
                            && uint.TryParse(parts[3], out uint w)
                            && uint.TryParse(parts[4], out uint h))
                        {
                            entries.Add((parts[0], x, y, w, h));
                        }
                        i++;
                    }
                    if (entries.Count > 0)
                        _arrangements[setKey] = entries;
                }
            }
            catch { /* best-effort */ }
        }

        private void SaveArrangementFile()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in _arrangements)
                {
                    sb.AppendLine(kv.Key);
                    foreach (var (model, x, y, w, h) in kv.Value)
                        sb.AppendLine($"{model}\t{x}\t{y}\t{w}\t{h}");
                    sb.AppendLine();
                }
                File.WriteAllText(_arrangementFilePath, sb.ToString());
            }
            catch { /* best-effort */ }
        }

        private void LoadLastResizeAction()
        {
            try
            {
                if (!File.Exists(_lastResizeFilePath)) return;
                foreach (var line in File.ReadAllLines(_lastResizeFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Trim().Split('\t');
                    if (parts.Length == 3
                        && (parts[1] == "right" || parts[1] == "left")
                        && int.TryParse(parts[2], out int pct)
                        && (pct == 50 || pct == 70 || pct == 30))
                    {
                        _lastResizeActions[parts[0]] = (parts[1] == "right", pct);
                    }
                }
            }
            catch { /* best-effort */ }
        }

        private void SaveLastResizeAction()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in _lastResizeActions)
                    sb.AppendLine($"{kv.Key}\t{(kv.Value.extendRight ? "right" : "left")}\t{kv.Value.percent}");
                File.WriteAllText(_lastResizeFilePath, sb.ToString());
            }
            catch { /* best-effort */ }
        }

        private void LoadIgnoreFile()
        {
            try
            {
                if (!File.Exists(_ignoreFilePath)) return;
                _ignoreRules.Clear();
                foreach (var line in File.ReadAllLines(_ignoreFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("exact:", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = trimmed["exact:".Length..].TrimStart();
                        _ignoreRules.Add(("exact", value));
                    }
                    else if (trimmed.StartsWith("contains:", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = trimmed["contains:".Length..].TrimStart();
                        _ignoreRules.Add(("contains", value));
                    }
                    else
                    {
                        // Legacy lines without prefix treated as exact match
                        _ignoreRules.Add(("exact", trimmed));
                    }
                }
            }
            catch { /* best-effort */ }
        }

        private void SaveIgnoreFile()
        {
            try
            {
                var lines = _ignoreRules.Select(r => $"{r.mode}: {r.pattern}");
                File.WriteAllLines(_ignoreFilePath, lines);
            }
            catch { /* best-effort */ }
        }

        private bool IsWindowIgnored(string processName, string title)
        {
            foreach (var (mode, pattern) in _ignoreRules)
            {
                if (mode == "exact" && string.Equals(pattern, processName, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (mode == "contains" && title.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private void WindowItem_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WindowItem.IsIgnored) && sender is WindowItem w)
            {
                if (w.IsIgnored)
                {
                    _ignoreRules.Add(("exact", w.ProcessName));
                }
                else
                {
                    _ignoreRules.RemoveAll(r => r.mode == "exact"
                        && string.Equals(r.pattern, w.ProcessName, StringComparison.OrdinalIgnoreCase));
                }
                SaveIgnoreFile();

                // Update all other items with the same process name
                foreach (var other in _windows)
                {
                    if (other != w && string.Equals(other.ProcessName, w.ProcessName, StringComparison.OrdinalIgnoreCase))
                        other.IsIgnored = w.IsIgnored;
                }
            }
        }

        private static string ExtractModel(string serial)
        {
            // Serial looks like "\\?\DISPLAY#HSJ1600#5&15c019df&0&UID281#{...}"
            var segs = serial.Split('#');
            return segs.Length >= 2 ? segs[1] : serial;
        }

        private static string ExtractUid(string serial)
        {
            // Extract "UID281" from "\\?\DISPLAY#HSJ1600#5&15c019df&0&UID281#{...}"
            var segs = serial.Split('#');
            if (segs.Length >= 3)
            {
                string instance = segs[2];
                int idx = instance.IndexOf("UID", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) return instance.Substring(idx);
            }
            return serial;
        }

        private string ComputeModelUid(string devicePath)
        {
            return $"{ExtractModel(devicePath)}-{ExtractUid(devicePath)}";
        }

        private string ComputeSetKey()
        {
            var ids = _monitors
                .Where(m => m.IsAttached)
                .Select(m => ComputeModelUid(m.DevicePath))
                .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return string.Join(",", ids);
        }

        private void BtnSaveArrangement_Click(object sender, RoutedEventArgs e)
        {
            var attached = _monitors.Where(m => m.IsAttached).ToList();
            if (attached.Count == 0) return;

            var entries = new List<(string model, int x, int y, uint w, uint h)>();
            foreach (var m in attached)
            {
                var dm = new NativeMethods.DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
                if (NativeMethods.EnumDisplaySettingsEx(m.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm, 0)
                    && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                {
                    entries.Add((ComputeModelUid(m.DevicePath), dm.dmPositionX, dm.dmPositionY, dm.dmPelsWidth, dm.dmPelsHeight));
                }
            }

            if (entries.Count == 0) return;

            string key = ComputeSetKey();
            _arrangements[key] = entries;
            SaveArrangementFile();
            ShowArrangementStatus($"Saved for {entries.Count} monitors.");
            BtnRestoreArrangement.IsEnabled = true;
        }

        private void BtnRestoreArrangement_Click(object sender, RoutedEventArgs e)
        {
            string key = ComputeSetKey();
            if (!_arrangements.TryGetValue(key, out var entries))
            {
                ShowArrangementStatus("No saved arrangement found.");
                return;
            }

            var attached = _monitors.Where(m => m.IsAttached).ToList();
            // Build a lookup from model-UID to saved position
            var lookup = new Dictionary<string, (int x, int y, uint w, uint h)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, x, y, w, h) in entries)
                lookup[id] = (x, y, w, h);

            int applied = 0;
            int failed = 0;
            foreach (var m in attached)
            {
                string id = ComputeModelUid(m.DevicePath);
                if (!lookup.TryGetValue(id, out var pos)) continue;

                var (sx, sy, sw, sh) = pos;
                var dm = new NativeMethods.DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
                dm.dmFields = NativeMethods.DM_POSITION | NativeMethods.DM_PELSWIDTH | NativeMethods.DM_PELSHEIGHT;
                dm.dmPositionX = sx;
                dm.dmPositionY = sy;
                dm.dmPelsWidth = sw;
                dm.dmPelsHeight = sh;

                int res = NativeMethods.ChangeDisplaySettingsEx(
                    m.DeviceName, ref dm, IntPtr.Zero,
                    NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_NORESET, IntPtr.Zero);
                if (res == NativeMethods.DISP_CHANGE_SUCCESSFUL)
                    applied++;
                else
                    failed++;
            }

            // Apply all pending changes
            int applyRes = NativeMethods.ChangeDisplaySettingsEx(
                null!, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

            if (applied > 0 && failed == 0 && applyRes == NativeMethods.DISP_CHANGE_SUCCESSFUL)
                ShowArrangementStatus($"Restored for {applied} monitors.");
            else if (applied > 0)
                ShowArrangementStatus($"Restored {applied}, failed {failed}, apply={applyRes}");
            else
                ShowArrangementStatus("Could not match monitors.");

            RefreshMonitors();
        }

        private DispatcherTimer? _statusTimer;

        private void ShowArrangementStatus(string text)
        {
            ArrangementStatus.Text = text;
            if (_statusTimer is null)
            {
                _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _statusTimer.Tick += (_, _) => { ArrangementStatus.Text = string.Empty; _statusTimer.Stop(); };
            }
            else
            {
                _statusTimer.Stop();
            }
            _statusTimer.Start();
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            _hWnd = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(_hWnd);
            _hwndSource.AddHook(WndProc);

            var hWnd = _hWnd;
            bool ok = NativeMethods.RegisterHotKey(hWnd, HotkeyId_MaxRight,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
                NativeMethods.VK_RIGHT);
            if (!ok)
                MessageBox.Show("Could not register hotkey Ctrl+Alt+Shift+Right.\nIt may already be in use by another app.",
                    "Hotkey conflict", MessageBoxButton.OK, MessageBoxImage.Warning);

            bool ok2 = NativeMethods.RegisterHotKey(hWnd, HotkeyId_MaxLeft,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
                NativeMethods.VK_LEFT);
            if (!ok2)
                MessageBox.Show("Could not register hotkey Ctrl+Alt+Shift+Left.\nIt may already be in use by another app.",
                    "Hotkey conflict", MessageBoxButton.OK, MessageBoxImage.Warning);

            bool ok3 = NativeMethods.RegisterHotKey(hWnd, HotkeyId_RestoreArr,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT | NativeMethods.MOD_NOREPEAT,
                NativeMethods.VK_UP);
            if (!ok3)
                MessageBox.Show("Could not register hotkey Ctrl+Alt+Shift+Up.\nIt may already be in use by another app.",
                    "Hotkey conflict", MessageBoxButton.OK, MessageBoxImage.Warning);

            // Low-level keyboard hook to intercept Win+Arrow (shell-reserved, can't use RegisterHotKey)
            _kbHookProc = LowLevelKeyboardHook;
            _kbHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL, _kbHookProc,
                NativeMethods.GetModuleHandle(null), 0);

            // Track foreground window changes system-wide (skip our own process)
            _winEventDelegate = OnForegroundChanged;
            _winEventHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventDelegate, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        }

        private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (hwnd != IntPtr.Zero && hwnd != _hWnd)
            {
                _previousForegroundHwnd = hwnd;
            }
        }

        private IntPtr GetHotkeyStartWindow()
        {
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != _hWnd)
                return foreground;

            if (_previousForegroundHwnd != IntPtr.Zero && _previousForegroundHwnd != _hWnd
                && NativeMethods.IsWindowVisible(_previousForegroundHwnd))
            {
                return _previousForegroundHwnd;
            }

            return IntPtr.Zero;
        }

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            NativeMethods.UnregisterHotKey(_hWnd, HotkeyId_MaxRight);
            NativeMethods.UnregisterHotKey(_hWnd, HotkeyId_MaxLeft);
            NativeMethods.UnregisterHotKey(_hWnd, HotkeyId_RestoreArr);
            if (_kbHook != IntPtr.Zero)
                NativeMethods.UnhookWindowsHookEx(_kbHook);
            if (_winEventHook != IntPtr.Zero)
                NativeMethods.UnhookWinEvent(_winEventHook);
            _hwndSource?.RemoveHook(WndProc);
            Application.Current.Shutdown();
            Environment.Exit(0);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DISPLAYCHANGE = 0x007E;
            if (msg == WM_DISPLAYCHANGE)
            {
                RefreshMonitors();
                handled = true;
            }
            else if (msg == NativeMethods.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HotkeyId_MaxRight)
                {
                    var target = RefreshWindows(GetHotkeyStartWindow());
                    if (target != IntPtr.Zero)
                        ResizeWindow(target, extendRight: true);
                    handled = true;
                }
                else if (id == HotkeyId_MaxLeft)
                {
                    var target = RefreshWindows(GetHotkeyStartWindow());
                    if (target != IntPtr.Zero)
                        ResizeWindow(target, extendRight: false);
                    handled = true;
                }
                else if (id == HotkeyId_RestoreArr)
                {
                    RestoreLastResizeAction();
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        private IntPtr LowLevelKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var kb = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

                if (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN)
                {
                    if (kb.vkCode == NativeMethods.VK_LWIN || kb.vkCode == NativeMethods.VK_RWIN)
                    {
                        _winKeyDown = true;
                    }

                    bool shiftDown = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_SHIFT) & 0x8000) != 0;
                    bool altDown = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_MENU) & 0x8000) != 0;
                    bool ctrlDown = (NativeMethods.GetAsyncKeyState((int)NativeMethods.VK_CONTROL) & 0x8000) != 0;

                    if (_winKeyDown && !shiftDown && !altDown && !ctrlDown && kb.vkCode == NativeMethods.VK_RIGHT)
                    {
                        var target = GetHotkeyStartWindow();
                        Dispatcher.BeginInvoke(() => SnapWindow(target, snapRight: true));
                        return (IntPtr)1;
                    }
                    if (_winKeyDown && !shiftDown && !altDown && !ctrlDown && kb.vkCode == NativeMethods.VK_LEFT)
                    {
                        var target = GetHotkeyStartWindow();
                        Dispatcher.BeginInvoke(() => SnapWindow(target, snapRight: false));
                        return (IntPtr)1;
                    }
                    if (_winKeyDown && !shiftDown && !altDown && !ctrlDown && kb.vkCode == NativeMethods.VK_UP)
                    {
                        var target = GetHotkeyStartWindow();
                        Dispatcher.BeginInvoke(() => SnapWindowVertical(target, snapTop: true));
                        return (IntPtr)1;
                    }
                    if (_winKeyDown && !shiftDown && !altDown && !ctrlDown && kb.vkCode == NativeMethods.VK_DOWN)
                    {
                        var target = GetHotkeyStartWindow();
                        Dispatcher.BeginInvoke(() => SnapWindowVertical(target, snapTop: false));
                        return (IntPtr)1;
                    }

                }
                else if (wParam == (IntPtr)NativeMethods.WM_KEYUP || wParam == (IntPtr)NativeMethods.WM_SYSKEYUP)
                {
                    if (kb.vkCode == NativeMethods.VK_LWIN || kb.vkCode == NativeMethods.VK_RWIN)
                    {
                        _winKeyDown = false;
                    }
                }
            }
            return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
        }

        // ── Event handlers ─────────────────────────────────────────────────

        private void Window_Activated(object sender, EventArgs e)
        {
            RefreshWindows();
            RefreshMonitors();
        }

        private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshWindows();
            RefreshMonitors();
        }

        private void BtnMaxRight_Click(object sender, RoutedEventArgs e) =>
            ResizeSelectedWindow(extendRight: true);

        private void BtnMaxLeft_Click(object sender, RoutedEventArgs e) =>
            ResizeSelectedWindow(extendRight: false);

        // ── Window enumeration ─────────────────────────────────────────────

        /// <summary>
        /// Walk the z-order from the foreground window, populating the list box
        /// with windows until (and including) the first non-ignored one.
        /// Returns the target hwnd to resize, or IntPtr.Zero if none found.
        /// </summary>
        private IntPtr RefreshWindows(IntPtr startHwnd = default)
        {
            // Prune caches: remove entries for HWNDs that no longer exist
            var staleHwnds = new List<IntPtr>();
            foreach (var h in _processNameCache.Keys)
            {
                if (!NativeMethods.IsWindowVisible(h))
                    staleHwnds.Add(h);
            }
            foreach (var h in staleHwnds)
            {
                _processNameCache.Remove(h);
                _iconCache.Remove(h);
            }

            _windows.Clear();

            var ownHwnd = new WindowInteropHelper(this).Handle;
            IntPtr target = IntPtr.Zero;

            // Start from the previously active window and walk the z-order,
            // collecting windows until (and including) the first non-ignored one.
            var hwnd = startHwnd != IntPtr.Zero
                ? startHwnd
                : _previousForegroundHwnd != IntPtr.Zero
                    ? _previousForegroundHwnd
                    : NativeMethods.GetForegroundWindow();

            while (hwnd != IntPtr.Zero)
            {
                if (hwnd != ownHwnd && IsAltTabWindow(hwnd))
                {
                    var title = GetWindowTitle(hwnd);
                    var procName = GetProcessName(hwnd);
                    var icon = GetWindowIcon(hwnd);
                    bool ignored = IsWindowIgnored(procName, title);

                    var item = new WindowItem
                    {
                        Hwnd        = hwnd,
                        Title       = title,
                        ProcessName = procName,
                        Icon        = icon,
                        IsIgnored   = ignored,
                    };
                    item.PropertyChanged += WindowItem_PropertyChanged;
                    _windows.Add(item);

                    if (!ignored)
                    {
                        target = hwnd;
                        break; // This is the resize target — stop here
                    }
                }
                hwnd = NativeMethods.GetWindow(hwnd, NativeMethods.GW_HWNDNEXT);
            }

            // Select the last item (the resize target)
            if (_windows.Count > 0)
                WindowListBox.SelectedIndex = _windows.Count - 1;

            return target;
        }

        // ── Resize logic ───────────────────────────────────────────────────

        private void ResizeSelectedWindow(bool extendRight)
        {
            if (WindowListBox.SelectedItem is not WindowItem target) return;
            ResizeWindow(target.Hwnd, extendRight);
        }

        private static bool IsRectApproximatelyEqual(NativeMethods.RECT left, NativeMethods.RECT right, int tolerance = 16)
        {
            return Math.Abs(left.Left - right.Left) <= tolerance
                && Math.Abs(left.Top - right.Top) <= tolerance
                && Math.Abs(left.Right - right.Right) <= tolerance
                && Math.Abs(left.Bottom - right.Bottom) <= tolerance;
        }

        private static void PlayBlockedFullscreenSound()
        {
            SystemSounds.Beep.Play();
        }

        private bool IsLikelyFullscreenWindow(IntPtr hwnd)
        {
            var placement = new NativeMethods.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            NativeMethods.GetWindowPlacement(hwnd, ref placement);
            if (placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED)
                return false;

            var hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfo(hMon, ref mi)
                || !NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                return false;
            }

            if (!IsRectApproximatelyEqual(rect, mi.rcMonitor))
                return false;

            long style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_STYLE);
            bool hasCaption = (style & NativeMethods.WS_CAPTION) != 0;
            bool hasResizeFrame = (style & NativeMethods.WS_THICKFRAME) != 0;
            return !hasCaption && !hasResizeFrame;
        }

        private bool TryBlockFullscreenWindow(IntPtr hwnd)
        {
            if (!IsLikelyFullscreenWindow(hwnd))
                return false;

            PlayBlockedFullscreenSound();
            return true;
        }

        private static bool IsSameMonitorRect(NativeMethods.RECT left, NativeMethods.RECT right)
        {
            return left.Left == right.Left
                && left.Top == right.Top
                && left.Right == right.Right
                && left.Bottom == right.Bottom;
        }

        private static int GetAxisOverlap(int startA, int endA, int startB, int endB)
        {
            return Math.Max(0, Math.Min(endA, endB) - Math.Max(startA, startB));
        }

        private bool TryGetAdjacentMonitorRect(NativeMethods.RECT currentMonitor, bool extendRight,
            out NativeMethods.RECT adjacentMonitor)
        {
            adjacentMonitor = default;
            bool found = false;
            int bestOverlap = -1;
            int bestGap = int.MaxValue;

            for (uint i = 0; ; i++)
            {
                var adapter = new NativeMethods.DISPLAY_DEVICE();
                adapter.cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>();
                if (!NativeMethods.EnumDisplayDevices(null, i, ref adapter, 0))
                    break;

                if ((adapter.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0)
                    continue;

                var dm = new NativeMethods.DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
                if (!NativeMethods.EnumDisplaySettingsEx(adapter.DeviceName,
                        NativeMethods.ENUM_CURRENT_SETTINGS, ref dm, 0)
                    || dm.dmPelsWidth == 0 || dm.dmPelsHeight == 0)
                {
                    continue;
                }

                var candidate = new NativeMethods.RECT
                {
                    Left = dm.dmPositionX,
                    Top = dm.dmPositionY,
                    Right = dm.dmPositionX + (int)dm.dmPelsWidth,
                    Bottom = dm.dmPositionY + (int)dm.dmPelsHeight,
                };

                if (IsSameMonitorRect(candidate, currentMonitor))
                    continue;

                bool isOnRequestedSide = extendRight
                    ? candidate.Left >= currentMonitor.Right
                    : candidate.Right <= currentMonitor.Left;
                if (!isOnRequestedSide)
                    continue;

                int overlap = GetAxisOverlap(currentMonitor.Top, currentMonitor.Bottom,
                    candidate.Top, candidate.Bottom);
                int gap = extendRight
                    ? candidate.Left - currentMonitor.Right
                    : currentMonitor.Left - candidate.Right;

                if (!found || overlap > bestOverlap || (overlap == bestOverlap && gap < bestGap))
                {
                    adjacentMonitor = candidate;
                    bestOverlap = overlap;
                    bestGap = gap;
                    found = true;
                }
            }

            return found;
        }

        private void ResizeWindow(IntPtr hwnd, bool extendRight)
        {
            if (TryBlockFullscreenWindow(hwnd))
                return;

            // If window is maximized, restore it first so SetWindowPos works correctly.
            // Track whether we restored from maximized — if so, skip the toggle
            // (maximize acts as a reset; always go to default 150%).
            var placement = new NativeMethods.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            NativeMethods.GetWindowPlacement(hwnd, ref placement);
            bool wasMaximized = placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED;
            if (wasMaximized)
            {
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            }

            // Match the target window's DPI awareness so that
            // GetMonitorInfo and SetWindowPos use the same coordinate space
            // and SetWindowPos does NOT auto-convert for cross-process DPI.
            var targetDpiCtx = NativeMethods.GetWindowDpiAwarenessContext(hwnd);
            var prevDpiCtx = NativeMethods.SetThreadDpiAwarenessContext(targetDpiCtx);
            try
            {
                // Get the monitor the target window is on
                var hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var mi = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (!NativeMethods.GetMonitorInfo(hMon, ref mi))
                {
                    MessageBox.Show("Could not get monitor info.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var mon = mi.rcMonitor;   // full physical screen rect
                var work = mi.rcWork;     // working area (excludes taskbar)
                int monW = mon.Width;
                int monH = mon.Height;

                int adjMonW = monW; // fallback to same monitor width
                int adjMonH = monH;
                // Find the best monitor on the requested side, even if displays are
                // vertically offset or separated by a gap.
                if (TryGetAdjacentMonitorRect(mon, extendRight, out var adjacentMonitor))
                {
                    adjMonW = adjacentMonitor.Width;
                    adjMonH = adjacentMonitor.Height;
                }

                // If the adjacent monitor is portrait (height > width), always use 100%
                bool adjIsPortrait = adjMonH > adjMonW;

                // Cycle 50% → 70% → 30% → 50% of the adjacent monitor's width,
                // but only if the window was NOT just restored from maximized
                // (maximize resets to default 50%).
                bool isHalf = false;
                bool is70 = false;
                if (!wasMaximized)
                {
                    NativeMethods.GetWindowRect(hwnd, out var curRect);
                    const int tolerance = 16;

                    if (extendRight)
                    {
                        int expectedHalfW = monW + adjMonW / 2;
                        isHalf = Math.Abs(curRect.Left - mon.Left) <= tolerance
                              && Math.Abs(curRect.Top - work.Top) <= tolerance
                              && Math.Abs(curRect.Width - expectedHalfW) <= tolerance
                              && Math.Abs(curRect.Height - work.Height) <= tolerance;

                        int expected70W = monW + adjMonW * 70 / 100;
                        is70 = Math.Abs(curRect.Left - mon.Left) <= tolerance
                              && Math.Abs(curRect.Top - work.Top) <= tolerance
                              && Math.Abs(curRect.Width - expected70W) <= tolerance
                              && Math.Abs(curRect.Height - work.Height) <= tolerance;
                    }
                    else
                    {
                        int expectedHalfW = monW + adjMonW / 2;
                        int expectedHalfL = mon.Left - adjMonW / 2;
                        isHalf = Math.Abs(curRect.Left - expectedHalfL) <= tolerance
                              && Math.Abs(curRect.Top - work.Top) <= tolerance
                              && Math.Abs(curRect.Width - expectedHalfW) <= tolerance
                              && Math.Abs(curRect.Height - work.Height) <= tolerance;

                        int expected70W = monW + adjMonW * 70 / 100;
                        int expected70L = mon.Left - adjMonW * 70 / 100;
                        is70 = Math.Abs(curRect.Left - expected70L) <= tolerance
                              && Math.Abs(curRect.Top - work.Top) <= tolerance
                              && Math.Abs(curRect.Width - expected70W) <= tolerance
                              && Math.Abs(curRect.Height - work.Height) <= tolerance;
                    }
                }

                // Cycle: 50% → 70% → 30% → 50% of adjacent monitor width
                // Portrait monitors always get 100%
                int percentOfAdj = adjIsPortrait ? 100 : (isHalf ? 70 : is70 ? 30 : 50);
                int extend = adjMonW * percentOfAdj / 100;

                // Persist this action per process so Ctrl+Alt+Shift+Up can replay it
                string procName = GetProcessName(hwnd);
                _lastResizeActions[procName] = (extendRight, percentOfAdj);
                SaveLastResizeAction();

                int left, top, width, height;
                top    = work.Top;
                height = work.Height;
                width  = monW + extend;

                if (extendRight)
                {
                    left = mon.Left;
                }
                else
                {
                    left = mon.Left - extend;
                }

                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                    left, top, width, height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
            finally
            {
                NativeMethods.SetThreadDpiAwarenessContext(prevDpiCtx);
            }
        }

        // ── Restore last resize action (Ctrl+Alt+Shift+Up) ────────────────

        private void RestoreLastResizeAction()
        {
            var target = RefreshWindows();
            if (target == IntPtr.Zero) return;
            if (TryBlockFullscreenWindow(target)) return;

            string procName = GetProcessName(target);
            if (!_lastResizeActions.TryGetValue(procName, out var action)) return;
            var (extendRight, percent) = action;

            // Restore if maximized so SetWindowPos works in raw coordinates
            var placement = new NativeMethods.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            NativeMethods.GetWindowPlacement(target, ref placement);
            if (placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED)
                NativeMethods.ShowWindow(target, NativeMethods.SW_RESTORE);

            var targetDpiCtx = NativeMethods.GetWindowDpiAwarenessContext(target);
            var prevDpiCtx = NativeMethods.SetThreadDpiAwarenessContext(targetDpiCtx);
            try
            {
                var hMon = NativeMethods.MonitorFromWindow(target, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var mi = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return;

                var mon = mi.rcMonitor;
                var work = mi.rcWork;
                int monW = mon.Width;
                int monH = mon.Height;

                int adjMonW = monW;
                int adjMonH = monH;
                if (TryGetAdjacentMonitorRect(mon, extendRight, out var adjacentMonitor))
                {
                    adjMonW = adjacentMonitor.Width;
                    adjMonH = adjacentMonitor.Height;
                }

                // Portrait monitors always get 100% regardless of saved percent
                int effectivePercent = (adjMonH > adjMonW) ? 100 : percent;
                int extend = adjMonW * effectivePercent / 100;
                int left   = extendRight ? mon.Left : mon.Left - extend;

                NativeMethods.SetWindowPos(target, IntPtr.Zero,
                    left, work.Top, monW + extend, work.Height,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
            finally
            {
                NativeMethods.SetThreadDpiAwarenessContext(prevDpiCtx);
            }
        }

        // ── Snap logic (Win+Arrow) ─────────────────────────────────────────

        private void SnapWindow(IntPtr hwnd, bool snapRight)
        {
            if (hwnd == IntPtr.Zero || hwnd == _hWnd) return;
            if (TryBlockFullscreenWindow(hwnd))
                return;

            // Restore if maximized
            var placement = new NativeMethods.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            NativeMethods.GetWindowPlacement(hwnd, ref placement);
            if (placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED)
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

            var targetDpiCtx = NativeMethods.GetWindowDpiAwarenessContext(hwnd);
            var prevDpiCtx = NativeMethods.SetThreadDpiAwarenessContext(targetDpiCtx);
            try
            {
                var hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var mi = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return;

                var work = mi.rcWork;
                int workW = work.Width;
                int workH = work.Height;

                // Detect current snap state
                NativeMethods.GetWindowRect(hwnd, out var cur);
                const int tol = 16;

                // Compute expected geometries for all three states
                int halfW = workW / 2;
                int thirdW = workW / 3;
                int twoThirdW = workW * 2 / 3;

                bool isHalf, isTwoThird, isOneThird;
                if (snapRight)
                {
                    int halfL   = work.Left + workW - halfW;
                    int ttL     = work.Left + workW - twoThirdW;
                    int otL     = work.Left + workW - thirdW;

                    isHalf     = Math.Abs(cur.Left - halfL) <= tol
                              && Math.Abs(cur.Top - work.Top) <= tol
                              && Math.Abs(cur.Width - halfW) <= tol
                              && Math.Abs(cur.Height - workH) <= tol;

                    isTwoThird = Math.Abs(cur.Left - ttL) <= tol
                              && Math.Abs(cur.Top - work.Top) <= tol
                              && Math.Abs(cur.Width - twoThirdW) <= tol
                              && Math.Abs(cur.Height - workH) <= tol;

                    isOneThird = Math.Abs(cur.Left - otL) <= tol
                              && Math.Abs(cur.Top - work.Top) <= tol
                              && Math.Abs(cur.Width - thirdW) <= tol
                              && Math.Abs(cur.Height - workH) <= tol;
                }
                else
                {
                    isHalf     = Math.Abs(cur.Left - work.Left) <= tol
                              && Math.Abs(cur.Top - work.Top) <= tol
                              && Math.Abs(cur.Width - halfW) <= tol
                              && Math.Abs(cur.Height - workH) <= tol;

                    isTwoThird = Math.Abs(cur.Left - work.Left) <= tol
                              && Math.Abs(cur.Top - work.Top) <= tol
                              && Math.Abs(cur.Width - twoThirdW) <= tol
                              && Math.Abs(cur.Height - workH) <= tol;

                    isOneThird = Math.Abs(cur.Left - work.Left) <= tol
                              && Math.Abs(cur.Top - work.Top) <= tol
                              && Math.Abs(cur.Width - thirdW) <= tol
                              && Math.Abs(cur.Height - workH) <= tol;
                }

                // Cycle: half → 2/3 → 1/3 → half
                int newW, newL;
                if (isHalf)
                {
                    newW = twoThirdW;
                }
                else if (isTwoThird)
                {
                    newW = thirdW;
                }
                else
                {
                    newW = halfW; // default / from 1/3 → half
                }

                if (snapRight)
                    newL = work.Left + workW - newW;
                else
                    newL = work.Left;

                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                    newL, work.Top, newW, workH,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
            finally
            {
                NativeMethods.SetThreadDpiAwarenessContext(prevDpiCtx);
            }
        }

        // ── Snap vertical logic (Win+Up / Win+Down) ─────────────────────

        private void SnapWindowVertical(IntPtr hwnd, bool snapTop)
        {
            if (hwnd == IntPtr.Zero || hwnd == _hWnd) return;
            if (TryBlockFullscreenWindow(hwnd))
                return;

            var placement = new NativeMethods.WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            NativeMethods.GetWindowPlacement(hwnd, ref placement);
            bool isMaximized = placement.showCmd == NativeMethods.SW_SHOWMAXIMIZED;

            if (snapTop)
            {
                // Win+Up cycle: full height → top 90% → top 50% → top 90% → …
                if (isMaximized)
                {
                    // Currently maximized → snap to top 90%
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

                    var targetDpiCtx = NativeMethods.GetWindowDpiAwarenessContext(hwnd);
                    var prevDpiCtx = NativeMethods.SetThreadDpiAwarenessContext(targetDpiCtx);
                    try
                    {
                        var hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                        var mi = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                        if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return;

                        var work = mi.rcWork;
                        int ninetyH = work.Height * 90 / 100;

                        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                            work.Left, work.Top, work.Width, ninetyH,
                            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                    }
                    finally
                    {
                        NativeMethods.SetThreadDpiAwarenessContext(prevDpiCtx);
                    }
                }
                else
                {
                    // Not maximized — detect top 90% vs top 50% to cycle, otherwise maximize
                    var targetDpiCtx = NativeMethods.GetWindowDpiAwarenessContext(hwnd);
                    var prevDpiCtx = NativeMethods.SetThreadDpiAwarenessContext(targetDpiCtx);
                    try
                    {
                        var hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                        var mi = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                        if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return;

                        var work = mi.rcWork;
                        int workH = work.Height;
                        int halfH = workH / 2;
                        int ninetyH = workH * 90 / 100;
                        const int tol = 16;

                        NativeMethods.GetWindowRect(hwnd, out var cur);

                        bool isTopNinety = Math.Abs(cur.Left - work.Left) <= tol
                                        && Math.Abs(cur.Top - work.Top) <= tol
                                        && Math.Abs(cur.Width - work.Width) <= tol
                                        && Math.Abs(cur.Height - ninetyH) <= tol;

                        bool isTopHalf = Math.Abs(cur.Left - work.Left) <= tol
                                      && Math.Abs(cur.Top - work.Top) <= tol
                                      && Math.Abs(cur.Width - work.Width) <= tol
                                      && Math.Abs(cur.Height - halfH) <= tol;

                        if (isTopNinety)
                        {
                            // top 90% → top 50%
                            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                                work.Left, work.Top, work.Width, halfH,
                                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                        }
                        else if (isTopHalf)
                        {
                            // top 50% → top 90%
                            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                                work.Left, work.Top, work.Width, ninetyH,
                                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                        }
                        else
                        {
                            // Any other state → maximize
                            NativeMethods.SetThreadDpiAwarenessContext(prevDpiCtx);
                            NativeMethods.ShowWindow(hwnd, (int)NativeMethods.SW_SHOWMAXIMIZED);
                            return;
                        }
                    }
                    finally
                    {
                        NativeMethods.SetThreadDpiAwarenessContext(prevDpiCtx);
                    }
                }
                return;
            }

            // snapTop == false (Win+Down): bottom 1/2 → bottom 2/3 → bottom 1/2 → …
            if (isMaximized)
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);

            var dpiCtx = NativeMethods.GetWindowDpiAwarenessContext(hwnd);
            var prevCtx = NativeMethods.SetThreadDpiAwarenessContext(dpiCtx);
            try
            {
                var hMon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
                var mi = new NativeMethods.MONITORINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (!NativeMethods.GetMonitorInfo(hMon, ref mi)) return;

                var work = mi.rcWork;
                int workW = work.Width;
                int workH = work.Height;
                int halfH = workH / 2;
                int seventyH = workH * 7 / 10;
                const int tol = 16;

                NativeMethods.GetWindowRect(hwnd, out var cur);

                int bottomHalfTop = work.Top + workH - halfH;
                bool isBottomHalf = Math.Abs(cur.Left - work.Left) <= tol
                            && Math.Abs(cur.Top - bottomHalfTop) <= tol
                            && Math.Abs(cur.Width - workW) <= tol
                            && Math.Abs(cur.Height - halfH) <= tol;

                int bottomSeventyTop = work.Top + workH - seventyH;
                bool isBottomSeventy = Math.Abs(cur.Left - work.Left) <= tol
                            && Math.Abs(cur.Top - bottomSeventyTop) <= tol
                            && Math.Abs(cur.Width - workW) <= tol
                            && Math.Abs(cur.Height - seventyH) <= tol;

                int newH, newT;
                if (isBottomHalf)
                {
                    // bottom 1/2 → bottom 70%
                    newH = seventyH;
                    newT = work.Top + workH - seventyH;
                }
                else if (isBottomSeventy)
                {
                    // bottom 70% → bottom 1/2
                    newH = halfH;
                    newT = work.Top + workH - halfH;
                }
                else
                {
                    // Any other state → bottom 1/2
                    newH = halfH;
                    newT = work.Top + workH - halfH;
                }

                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero,
                    work.Left, newT, workW, newH,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }
            finally
            {
                NativeMethods.SetThreadDpiAwarenessContext(prevCtx);
            }
        }

        // ── Monitor logic ──────────────────────────────────────────────────

        private void MonitorListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (MonitorListBox.SelectedItem is MonitorItem m)
            {
                BtnDetach.IsEnabled = m.IsAttached;
                BtnAttach.IsEnabled = !m.IsAttached;
            }
            else
            {
                BtnDetach.IsEnabled = false;
                BtnAttach.IsEnabled = false;
            }
        }

        private void BtnDetach_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorListBox.SelectedItem is not MonitorItem m || !m.IsAttached) return;

            // Detach: set position to 0,0 with zero pels, then apply
            var dm = new NativeMethods.DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
            dm.dmFields = NativeMethods.DM_POSITION | NativeMethods.DM_PELSWIDTH | NativeMethods.DM_PELSHEIGHT;
            dm.dmPelsWidth = 0;
            dm.dmPelsHeight = 0;

            int res = NativeMethods.ChangeDisplaySettingsEx(
                m.DeviceName, ref dm, IntPtr.Zero,
                NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_NORESET, IntPtr.Zero);

            // Apply all pending changes
            NativeMethods.ChangeDisplaySettingsEx(
                null!, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

            if (res != NativeMethods.DISP_CHANGE_SUCCESSFUL)
                MessageBox.Show($"Failed to detach {m.DeviceName} (error {res}).",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);

            RefreshMonitors();
        }

        /// <summary>
        /// Attach a specific physical monitor using CCD APIs (QueryDisplayConfig / SetDisplayConfig).
        /// Unlike ChangeDisplaySettingsEx (which only knows adapter names like "DISPLAY4"),
        /// CCD can distinguish individual physical monitors by their unique target ID.
        ///
        /// Parameters:
        ///   targetDevicePath — the full device interface path to match (e.g. "\\?\DISPLAY#HSJ1600#...").
        ///   matchModel       — (optional) if set, also try matching by monitor model name (e.g. "HSJ1600").
        ///                      Used for phantom monitors whose device path is fabricated and won't match any real path.
        ///   excludePaths     — (optional) device paths to skip during model-based matching,
        ///                      so we don't accidentally match a monitor that's already reported/attached.
        ///
        /// Returns true on success, false if no matching inactive target was found or SetDisplayConfig failed.
        /// </summary>
        private bool TryAttachWithCcd(string targetDevicePath, string? matchModel = null, HashSet<string>? excludePaths = null)
        {
            // ── Step 1: Query ALL paths (active + inactive) ──
            // QDC_ALL_PATHS includes every GPU-output↔monitor combination Windows knows about,
            // even monitors that are physically connected but not currently active ("inactive paths").
            int err = NativeMethods.GetDisplayConfigBufferSizes(
                NativeMethods.QDC_ALL_PATHS, out uint allPathCount, out uint allModeCount);
            if (err != 0) return false;

            var allPaths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[allPathCount];
            var allModes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[allModeCount];
            err = NativeMethods.QueryDisplayConfig(
                NativeMethods.QDC_ALL_PATHS, ref allPathCount, allPaths,
                ref allModeCount, allModes, IntPtr.Zero);
            if (err != 0) return false;

            // ── Step 2: Search inactive paths for our target monitor ──
            // We iterate all paths looking for an inactive one whose physical monitor matches.
            // Two matching strategies (exact match takes priority):
            //   a) Exact device path — used for real monitors with known paths.
            //   b) Model name match  — used for phantom monitors (fabricated paths that don't exist in CCD).
            //      Excludes already-reported paths so we don't re-match a monitor that's already active elsewhere.
            int matchIdx = -1;
            for (int i = 0; i < allPathCount; i++)
            {
                // Skip targets that aren't physically available (not connected)
                if (allPaths[i].targetInfo.targetAvailable == 0) continue;
                // Skip paths that are already active (monitor already attached)
                if ((allPaths[i].flags & NativeMethods.DISPLAYCONFIG_PATH_ACTIVE) != 0) continue;

                // Query the monitor's device name for this path
                var tn = new NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                tn.header.type = NativeMethods.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                tn.header.size = (uint)Marshal.SizeOf<NativeMethods.DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                tn.header.adapterId = allPaths[i].targetInfo.adapterId;
                tn.header.id = allPaths[i].targetInfo.id;

                if (NativeMethods.DisplayConfigGetDeviceInfo(ref tn) != 0) continue;

                // (a) Exact device path match — best possible match, stop immediately
                if (string.Equals(tn.monitorDevicePath, targetDevicePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matchIdx = i;
                    break;
                }

                // (b) Model-based match — for phantom monitors whose path is fabricated.
                //     We record this as a candidate but keep searching in case an exact match exists later.
                if (matchModel is not null
                    && !string.IsNullOrEmpty(tn.monitorDevicePath)
                    && ExtractModel(tn.monitorDevicePath).Equals(matchModel, StringComparison.OrdinalIgnoreCase)
                    && (excludePaths is null || !excludePaths.Contains(tn.monitorDevicePath)))
                {
                    matchIdx = i;
                    // Don't break — keep looking for an exact match
                }
            }

            if (matchIdx < 0) return false;

            // ── Step 3: Query current active configuration ──
            // We need the currently active paths and their modes so we can append our new path
            // without disturbing existing monitors.
            err = NativeMethods.GetDisplayConfigBufferSizes(
                NativeMethods.QDC_ONLY_ACTIVE_PATHS, out uint activePathCount, out uint activeModeCount);
            if (err != 0) return false;

            var activePaths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[activePathCount];
            var activeModes = new NativeMethods.DISPLAYCONFIG_MODE_INFO[activeModeCount];
            err = NativeMethods.QueryDisplayConfig(
                NativeMethods.QDC_ONLY_ACTIVE_PATHS, ref activePathCount, activePaths,
                ref activeModeCount, activeModes, IntPtr.Zero);
            if (err != 0) return false;

            // ── Step 4: Build combined path array = existing active paths + new path ──
            var newPath = allPaths[matchIdx];
            newPath.flags |= NativeMethods.DISPLAYCONFIG_PATH_ACTIVE;

            // Set mode indices to INVALID so Windows auto-selects resolution/refresh for the new monitor
            newPath.sourceInfo.modeInfoIdx = NativeMethods.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            newPath.targetInfo.modeInfoIdx = NativeMethods.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

            // Assign a free source ID so the new monitor gets its own desktop area
            // (rather than cloning an existing monitor's output)
            var usedSourceIds = new HashSet<uint>();
            for (int i = 0; i < activePathCount; i++)
                usedSourceIds.Add(activePaths[i].sourceInfo.id);
            for (uint freeId = 0; freeId < 32; freeId++)
            {
                if (!usedSourceIds.Contains(freeId))
                {
                    newPath.sourceInfo.id = freeId;
                    break;
                }
            }

            var combinedPaths = new NativeMethods.DISPLAYCONFIG_PATH_INFO[activePathCount + 1];
            Array.Copy(activePaths, 0, combinedPaths, 0, (int)activePathCount);
            combinedPaths[activePathCount] = newPath;

            // ── Step 5: Apply the new configuration ──
            // SDC_USE_SUPPLIED_DISPLAY_CONFIG — use exactly the paths/modes we provide
            // SDC_ALLOW_CHANGES              — let Windows adjust modes for the new path (since we set INVALID)
            // SDC_SAVE_TO_DATABASE            — persist the change so it survives reboot
            err = NativeMethods.SetDisplayConfig(
                (uint)combinedPaths.Length, combinedPaths,
                activeModeCount, activeModes,
                NativeMethods.SDC_APPLY
                | NativeMethods.SDC_USE_SUPPLIED_DISPLAY_CONFIG
                | NativeMethods.SDC_ALLOW_CHANGES
                | NativeMethods.SDC_SAVE_TO_DATABASE);

            return err == 0;
        }

        private void BtnShowLayout_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new LayoutDialog(_monitors.ToList()) { Owner = this };
            dlg.Show();
        }

        private void BtnFineTuneEdges_Click(object sender, RoutedEventArgs e)
        {
            var attached = _monitors.Where(m => m.IsAttached).ToList();
            if (attached.Count < 2) return;

            // Read current positions into a mutable list
            var rects = new List<(MonitorItem mon, int x, int y, int w, int h, uint orientation)>();
            foreach (var m in attached)
            {
                var dm = new NativeMethods.DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
                if (NativeMethods.EnumDisplaySettingsEx(m.DeviceName, NativeMethods.ENUM_CURRENT_SETTINGS, ref dm, 0)
                    && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                {
                    rects.Add((m, dm.dmPositionX, dm.dmPositionY, (int)dm.dmPelsWidth, (int)dm.dmPelsHeight, dm.dmDisplayOrientation));
                }
            }

            if (rects.Count < 2) return;

            // Save original positions for revert
            var originalPositions = rects.Select(r => (r.mon.DeviceName, r.x, r.y, r.w, r.h, r.orientation)).ToList();

            var snapLog = new StringBuilder();
            Debug.WriteLine("=== Snap displays ===");
            snapLog.AppendLine("=== Before ===");
            foreach (var r in rects)
            {
                Debug.WriteLine($"  Before: {r.mon.DeviceName} ({r.mon.Description}) @ ({r.x},{r.y}) {r.w}x{r.h}");
                snapLog.AppendLine($"{r.mon.DeviceName} ({r.mon.Description}) @ ({r.x},{r.y}) {r.w}x{r.h}");
            }
            snapLog.AppendLine();

            const int threshold = 100; // pixels — edges closer than this get snapped
            bool changed = true;

            // Iterate until no more adjustments (edges may cascade)
            while (changed)
            {
                changed = false;
                for (int i = 0; i < rects.Count; i++)
                {
                    for (int j = i + 1; j < rects.Count; j++)
                    {
                        var a = rects[i];
                        var b = rects[j];

                        int aRight = a.x + a.w;
                        int aBottom = a.y + a.h;
                        int bRight = b.x + b.w;
                        int bBottom = b.y + b.h;

                        // Top-to-top: align b's top to a's top
                        if (a.y != b.y && Math.Abs(a.y - b.y) <= threshold)
                        {
                            var msg = $"Snap top-top: {b.mon.DeviceName} top {b.y} → {a.y} (matched {a.mon.DeviceName})";
                            Debug.WriteLine($"  {msg}");
                            snapLog.AppendLine(msg);
                            b.y = a.y;
                            rects[j] = b;
                            changed = true;
                        }
                        // Bottom-to-bottom: align b's bottom to a's bottom → adjust b.y
                        else if (aBottom != bBottom && Math.Abs(aBottom - bBottom) <= threshold)
                        {
                            var msg = $"Snap bottom-bottom: {b.mon.DeviceName} top {b.y} → {aBottom - b.h} (matched {a.mon.DeviceName})";
                            Debug.WriteLine($"  {msg}");
                            snapLog.AppendLine(msg);
                            b.y = aBottom - b.h;
                            rects[j] = b;
                            changed = true;
                        }
                        // a's bottom close to b's top (stacked vertically)
                        else if (aBottom != b.y && Math.Abs(aBottom - b.y) <= threshold)
                        {
                            var msg = $"Snap {a.mon.DeviceName}.bottom→{b.mon.DeviceName}.top: top {b.y} → {aBottom}";
                            Debug.WriteLine($"  {msg}");
                            snapLog.AppendLine(msg);
                            b.y = aBottom;
                            rects[j] = b;
                            changed = true;
                        }
                        // a's top close to b's bottom (stacked vertically, b above a)
                        else if (a.y != bBottom && Math.Abs(a.y - bBottom) <= threshold)
                        {
                            var msg = $"Snap {a.mon.DeviceName}.top→{b.mon.DeviceName}.bottom: top {b.y} → {a.y - b.h}";
                            Debug.WriteLine($"  {msg}");
                            snapLog.AppendLine(msg);
                            b.y = a.y - b.h;
                            rects[j] = b;
                            changed = true;
                        }

                        // Recalculate after potential y change
                        bRight = b.x + b.w;

                        // Left-to-left: align b's left to a's left
                        if (a.x != b.x && Math.Abs(a.x - b.x) <= threshold)
                        {
                            var msg = $"Snap left-left: {b.mon.DeviceName} left {b.x} → {a.x} (matched {a.mon.DeviceName})";
                            Debug.WriteLine($"  {msg}");
                            snapLog.AppendLine(msg);
                            b.x = a.x;
                            rects[j] = b;
                            changed = true;
                        }
                        // Right-to-right: align b's right to a's right → adjust b.x
                        else if (aRight != bRight && Math.Abs(aRight - bRight) <= threshold)
                        {
                            var msg = $"Snap right-right: {b.mon.DeviceName} left {b.x} → {aRight - b.w} (matched {a.mon.DeviceName})";
                            Debug.WriteLine($"  {msg}");
                            snapLog.AppendLine(msg);
                            b.x = aRight - b.w;
                            rects[j] = b;
                            changed = true;
                        }
                        // a's right close to b's left (side by side)
                        else if (aRight != b.x && Math.Abs(aRight - b.x) <= threshold)
                        {
                            var msg = $"Snap {a.mon.DeviceName}.right→{b.mon.DeviceName}.left: left {b.x} → {aRight}";
                            Debug.WriteLine($"  {msg}");
                            snapLog.AppendLine(msg);
                            b.x = aRight;
                            rects[j] = b;
                            changed = true;
                        }
                        // a's left close to b's right (b is to the left of a)
                        else if (a.x != bRight && Math.Abs(a.x - bRight) <= threshold)
                        {
                            var msg = $"Snap {a.mon.DeviceName}.left→{b.mon.DeviceName}.right: left {b.x} → {a.x - b.w}";
                            Debug.WriteLine($"  {msg}");
                            snapLog.AppendLine(msg);
                            b.x = a.x - b.w;
                            rects[j] = b;
                            changed = true;
                        }
                    }
                }
            }

            // Apply the adjusted positions
            snapLog.AppendLine();
            snapLog.AppendLine("=== After ===");
            Debug.WriteLine("  After snapping:");
            foreach (var r in rects)
            {
                Debug.WriteLine($"    {r.mon.DeviceName} ({r.mon.Description}) → ({r.x},{r.y}) {r.w}x{r.h}");
                snapLog.AppendLine($"{r.mon.DeviceName} ({r.mon.Description}) → ({r.x},{r.y}) {r.w}x{r.h}");
            }

            int applied = 0;
            foreach (var (m, x, y, w, h, orientation) in rects)
            {
                var dm = new NativeMethods.DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
                dm.dmFields = NativeMethods.DM_POSITION | NativeMethods.DM_PELSWIDTH | NativeMethods.DM_PELSHEIGHT
                    | NativeMethods.DM_DISPLAYORIENTATION;
                dm.dmPositionX = x;
                dm.dmPositionY = y;
                dm.dmPelsWidth = (uint)w;
                dm.dmPelsHeight = (uint)h;
                dm.dmDisplayOrientation = orientation;

                int res = NativeMethods.ChangeDisplaySettingsEx(
                    m.DeviceName, ref dm, IntPtr.Zero,
                    NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_NORESET, IntPtr.Zero);
                if (res == NativeMethods.DISP_CHANGE_SUCCESSFUL)
                    applied++;
            }

            NativeMethods.ChangeDisplaySettingsEx(
                null!, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

            ShowArrangementStatus(applied > 0 ? $"Fine-tuned {applied} displays." : "No changes needed.");
            RefreshMonitors();

            var dlg = new SnapResultDialog(snapLog.ToString(), originalPositions, RefreshMonitors) { Owner = this };
            dlg.Show();
        }

        private void BtnDisplaySettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:display",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to open Display settings: " + ex.Message,
                    "ResizeWidth", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnAttach_Click(object sender, RoutedEventArgs e)
        {
            if (MonitorListBox.SelectedItem is not MonitorItem m || m.IsAttached) return;

            // Try CCD first — it can target a specific physical monitor by unique target ID,
            // which ChangeDisplaySettingsEx cannot do (it only knows adapter names like DISPLAY4).
            if (!string.IsNullOrEmpty(m.DevicePath))
            {
                bool ccdOk;
                if (m.IsUnreported)
                {
                    // Phantom monitor: match by model name, excluding already-reported paths
                    string model = ExtractModel(m.DevicePath);
                    var excludePaths = new HashSet<string>(
                        _monitors.Where(x => !x.IsUnreported).Select(x => x.DevicePath),
                        StringComparer.OrdinalIgnoreCase);
                    ccdOk = TryAttachWithCcd(m.DevicePath, model, excludePaths);
                }
                else
                {
                    ccdOk = TryAttachWithCcd(m.DevicePath);
                }

                if (ccdOk)
                {
                    RefreshMonitors();
                    return;
                }
            }

            string deviceName = m.DeviceName;
            ShowArrangementStatus($"Fallback attach: {deviceName}");

            // Re-attach: read last-known (registry) settings and apply them
            var dm = new NativeMethods.DEVMODE();
            dm.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
            if (!NativeMethods.EnumDisplaySettingsEx(deviceName, NativeMethods.ENUM_REGISTRY_SETTINGS, ref dm, 0))
            {
                MessageBox.Show($"Could not read saved settings for {deviceName}.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // If registry has 0×0 (overwritten by detach), use known resolution
            // for phantom HSJ1600 or enumerate best supported mode.
            if (dm.dmPelsWidth == 0 || dm.dmPelsHeight == 0)
            {
                if (m.IsUnreported && ExtractModel(m.DevicePath).Equals("HSJ1600", StringComparison.OrdinalIgnoreCase))
                {
                    dm.dmPelsWidth = 3072;
                    dm.dmPelsHeight = 1920;
                }
                else
                {
                    // Fallback: enumerate supported modes and pick the best one
                    var best = new NativeMethods.DEVMODE();
                    best.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
                    var candidate = new NativeMethods.DEVMODE();
                    candidate.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
                    bool found = false;

                    for (int i = 0; NativeMethods.EnumDisplaySettingsEx(deviceName, i, ref candidate, 0); i++)
                    {
                        if (!found
                            || candidate.dmPelsWidth * candidate.dmPelsHeight > best.dmPelsWidth * best.dmPelsHeight
                            || (candidate.dmPelsWidth == best.dmPelsWidth
                                && candidate.dmPelsHeight == best.dmPelsHeight
                                && candidate.dmDisplayFrequency > best.dmDisplayFrequency))
                        {
                            best = candidate;
                            found = true;
                        }
                    }

                    if (!found)
                    {
                        MessageBox.Show($"No supported display modes found for {deviceName}.",
                            "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }

                    dm.dmPelsWidth = best.dmPelsWidth;
                    dm.dmPelsHeight = best.dmPelsHeight;
                }
            }

            dm.dmFields = NativeMethods.DM_POSITION | NativeMethods.DM_PELSWIDTH | NativeMethods.DM_PELSHEIGHT;

            int res = NativeMethods.ChangeDisplaySettingsEx(
                deviceName, ref dm, IntPtr.Zero,
                NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_NORESET, IntPtr.Zero);

            // If BADMODE, the stored resolution may not match what Windows thinks
            // is connected (stale cache). Fall back to best supported mode.
            if (res == NativeMethods.DISP_CHANGE_BADMODE)
            {
                var best = new NativeMethods.DEVMODE();
                best.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
                var candidate = new NativeMethods.DEVMODE();
                candidate.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();
                bool found = false;

                for (int i = 0; NativeMethods.EnumDisplaySettingsEx(deviceName, i, ref candidate, 0); i++)
                {
                    if (!found
                        || candidate.dmPelsWidth * candidate.dmPelsHeight > best.dmPelsWidth * best.dmPelsHeight
                        || (candidate.dmPelsWidth == best.dmPelsWidth
                            && candidate.dmPelsHeight == best.dmPelsHeight
                            && candidate.dmDisplayFrequency > best.dmDisplayFrequency))
                    {
                        best = candidate;
                        found = true;
                    }
                }

                if (found)
                {
                    dm.dmPelsWidth = best.dmPelsWidth;
                    dm.dmPelsHeight = best.dmPelsHeight;
                    res = NativeMethods.ChangeDisplaySettingsEx(
                        deviceName, ref dm, IntPtr.Zero,
                        NativeMethods.CDS_UPDATEREGISTRY | NativeMethods.CDS_NORESET, IntPtr.Zero);
                }
            }

            // Apply all pending changes
            NativeMethods.ChangeDisplaySettingsEx(
                null!, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

            if (res != NativeMethods.DISP_CHANGE_SUCCESSFUL)
            {
                MessageBox.Show($"Failed to attach {deviceName} (error {res}).",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            RefreshMonitors();
        }

        private void RefreshMonitors()
        {
            var prevDevice = (MonitorListBox.SelectedItem as MonitorItem)?.DeviceName;
            _monitors.Clear();
            var reportedSerials = new HashSet<string>();
            var seenDetachedSerials = new HashSet<string>();

            var adapter = new NativeMethods.DISPLAY_DEVICE();
            adapter.cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>();

            for (uint i = 0; NativeMethods.EnumDisplayDevices(null, i, ref adapter, 0); i++)
            {
                var adapterName = adapter.DeviceName;

                // Get child monitor device for brand/model/device path
                var monitor = new NativeMethods.DISPLAY_DEVICE();
                monitor.cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>();
                string description = adapter.DeviceString;
                string devicePath = string.Empty;

                if (NativeMethods.EnumDisplayDevices(adapterName, 0, ref monitor,
                        NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME))
                {
                    if (!string.IsNullOrWhiteSpace(monitor.DeviceString))
                        description = monitor.DeviceString;
                    if (!string.IsNullOrWhiteSpace(monitor.DeviceID))
                        devicePath = monitor.DeviceID;
                }

                // Skip adapter outputs with no known physical monitor
                // (e.g. iGPU ports that have never had a display connected).
                if (string.IsNullOrEmpty(devicePath))
                    continue;

                bool attached = (adapter.StateFlags & NativeMethods.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) != 0;

                reportedSerials.Add(devicePath);

                // Deduplicate detached monitors with the same device instance path
                if (!attached && !seenDetachedSerials.Add(devicePath))
                    continue;

                // Get resolution
                string resolution = string.Empty;
                var dm = new NativeMethods.DEVMODE();
                dm.dmSize = (ushort)Marshal.SizeOf<NativeMethods.DEVMODE>();

                int modeIndex = attached
                    ? NativeMethods.ENUM_CURRENT_SETTINGS
                    : NativeMethods.ENUM_REGISTRY_SETTINGS;

                if (NativeMethods.EnumDisplaySettingsEx(adapterName, modeIndex, ref dm, 0))
                {
                    resolution = $"{dm.dmPelsWidth}×{dm.dmPelsHeight}";
                }

                _monitors.Add(new MonitorItem
                {
                    DeviceName  = adapterName,
                    Description = description,
                    DevicePath  = devicePath,
                    Resolution  = resolution,
                    IsAttached  = attached,
                });
            }

            // Add a phantom 5th HSJ1600 monitor when:
            // 1. CMN1636 (laptop screen) is reported but NOT attached
            // 2. There are fewer than 2 HSJ1600 monitors reported
            var cmn1636 = _monitors.FirstOrDefault(m =>
                ExtractModel(m.DevicePath).Equals("CMN1636", StringComparison.OrdinalIgnoreCase));
            if (cmn1636 is not null && !cmn1636.IsAttached)
            {
                int reportedHsj = _monitors.Count(m =>
                    ExtractModel(m.DevicePath).Equals("HSJ1600", StringComparison.OrdinalIgnoreCase));
                if (reportedHsj < 2)
                {
                    string phantomPath = cmn1636.DevicePath.Replace("CMN1636", "HSJ1600");
                    _monitors.Add(new MonitorItem
                    {
                        DeviceName  = cmn1636.DeviceName,
                        Description = "Generic PnP Monitor",
                        DevicePath  = phantomPath,
                        Resolution  = "3072×1920 (Unreported)",
                        IsAttached  = false,
                        IsUnreported = true,
                    });
                }
            }

            // Restore selection
            bool restored = false;
            if (prevDevice is not null)
            {
                foreach (var m in _monitors)
                {
                    if (m.DeviceName == prevDevice)
                    {
                        MonitorListBox.SelectedItem = m;
                        restored = true;
                        break;
                    }
                }
            }
            if (!restored && _monitors.Count > 0)
                MonitorListBox.SelectedIndex = 0;

            // Enable/disable Restore arrangement button based on whether a saved arrangement exists
            BtnRestoreArrangement.IsEnabled = _arrangements.ContainsKey(ComputeSetKey());
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private static bool IsAltTabWindow(IntPtr hWnd)
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return false;
            if (NativeMethods.GetWindowTextLength(hWnd) == 0) return false;

            long exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
            long style   = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_STYLE);

            // Skip child windows
            if ((style & NativeMethods.WS_CHILD) != 0) return false;

            // Skip tool windows (unless they also have WS_EX_APPWINDOW)
            bool isToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
            bool isAppWindow  = (exStyle & NativeMethods.WS_EX_APPWINDOW)  != 0;
            if (isToolWindow && !isAppWindow) return false;

            // Skip cloaked windows (UWP tiles, virtual desktops, etc.)
            NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
                out int cloaked, sizeof(int));
            if (cloaked != 0) return false;

            return true;
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            int len = NativeMethods.GetWindowTextLength(hWnd);
            if (len == 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private string GetProcessName(IntPtr hWnd)
        {
            if (_processNameCache.TryGetValue(hWnd, out var cached))
                return cached;
            try
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                using var proc = Process.GetProcessById((int)pid);
                var name = proc.ProcessName;
                _processNameCache[hWnd] = name;
                return name;
            }
            catch { return string.Empty; }
        }

        private BitmapSource? GetWindowIcon(IntPtr hWnd)
        {
            if (_iconCache.TryGetValue(hWnd, out var cached))
                return cached;
            try
            {
                // Use only GetClassLongPtr — no cross-process messages, no deadlock risk.
                IntPtr hIcon = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCL_HICONSM);
                if (hIcon == IntPtr.Zero)
                    hIcon = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCL_HICON);

                if (hIcon == IntPtr.Zero)
                {
                    _iconCache[hWnd] = null;
                    return null;
                }

                var bmp = Imaging.CreateBitmapSourceFromHIcon(hIcon,
                    System.Windows.Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze(); // Allow cross-thread use and reduce GC pressure
                _iconCache[hWnd] = bmp;
                return bmp;
            }
            catch
            {
                _iconCache[hWnd] = null;
                return null;
            }
        }
    }
}
