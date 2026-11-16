using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Screen = System.Windows.Forms.Screen;

namespace WinDesktopCube;

/// <summary>
/// Runs a desktop switch:
///  1. photograph the current desktop on each monitor
///  2. cover each monitor with a pinned overlay showing that photo as the cube's front face
///  3. switch the virtual desktop underneath (no Windows animation; the overlay hides it)
///  4. photograph the new desktop (the overlay is excluded from capture) and use it as the side face
///  5. rotate the cube 90 degrees, then remove the overlay to reveal the real desktop
/// </summary>
internal sealed class CubeController
{
    private readonly Dispatcher _dispatcher;
    private readonly Settings _settings;
    private readonly List<CubeOverlay> _overlays = new();
    public static bool DebugSnapshots;
    private bool _busy;
    private int _pendingSteps;
    private int _stepCounter;
    private double _lastFps;

    public CubeController(Dispatcher dispatcher, Settings settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        SyncMonitors();
        SystemEvents.DisplaySettingsChanged += (_, _) => _dispatcher.BeginInvoke(SyncMonitors);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.Desktop) _dispatcher.BeginInvoke(LoadWallpaper);
        };
    }

    /// <summary>Safe to call from any thread, including the keyboard hook callback.</summary>
    public void Request(int direction) => _dispatcher.BeginInvoke(() =>
    {
        _pendingSteps += direction;
        if (!_busy) _ = RunAsync();
    });

    private async Task RunAsync()
    {
        _busy = true;
        try
        {
            while (_pendingSteps != 0)
            {
                int dir = Math.Sign(_pendingSteps);
                _pendingSteps -= dir;
                await StepAsync(dir);
            }
        }
        catch (Exception e) { Log.Write($"Switch failed: {e}"); }
        finally
        {
            HideOverlays();
            _busy = false;
        }
    }

    private async Task StepAsync(int dir)
    {
        int count = VDA.GetDesktopCount();
        int current = VDA.GetCurrentDesktopNumber();
        if (count <= 1 || current < 0) return;

        int target = current + dir;
        bool atEdge = target < 0 || target >= count;
        if (atEdge && !_settings.WrapAround)
        {
            await BounceAsync(dir);
            return;
        }
        target = (target + count) % count;

        var sw = Stopwatch.StartNew();
        foreach (var o in _overlays) o.Prepare(ScreenCapture.Capture(o.Bounds), dir);
        await ShowOverlaysAsync();
        long tShown = sw.ElapsedMilliseconds;

        if (VDA.GoToDesktopNumber(target) < 0) Log.Write($"GoToDesktopNumber({target}) failed");
        await Task.Delay(_settings.CaptureDelayMs);
        await CaptureTargetsAsync();
        long tCaptured = sw.ElapsedMilliseconds;

        // Explorer repaints the per-desktop wallpaper (and some apps their content) a little
        // after the switch, so refresh the target face a couple of times while the cube turns.
        int stepId = ++_stepCounter;
        _ = RefreshTargetsAsync(stepId);

        double aspect = _overlays.Count > 0 ? (double)_overlays[0].Bounds.Width / _overlays[0].Bounds.Height : 16.0 / 9;
        int snaps = 0;
        await AnimateAsync(_settings.AnimationMs, t =>
        {
            double angle = -dir * 90 * EaseInOutCubic(t);
            double zoom = Math.Sin(Math.PI * t) * _settings.ZoomOut * aspect;
            foreach (var o in _overlays) o.SetPose(angle, zoom);
            if (DebugSnapshots && snaps < 3 && t >= snaps * 0.3 + 0.2)
            {
                snaps++;
                _overlays[0].UpdateLayout();
                _overlays[0].SaveSnapshot(Path.Combine(Settings.Dir, $"snap_{dir:+0;-0}_{snaps}.png"));
            }
        });

        _stepCounter++; // stop the refresh loop
        Log.Write($"Switched {current}->{target}: overlay up {tShown}ms, target captured {tCaptured}ms, " +
                  $"done {sw.ElapsedMilliseconds}ms, {_lastFps:F0} fps");

        if (_pendingSteps == 0)
        {
            HideOverlays();
            FocusTopWindow();
        }
    }

    /// <summary>Capture every monitor on a worker thread so the animation doesn't stutter.</summary>
    private async Task CaptureTargetsAsync()
    {
        var bounds = _overlays.Select(o => o.Bounds).ToArray();
        var shots = await Task.Run(() => bounds.Select(ScreenCapture.Capture).ToArray());
        for (int i = 0; i < shots.Length && i < _overlays.Count; i++) _overlays[i].SetTarget(shots[i]);
    }

    private async Task RefreshTargetsAsync(int stepId)
    {
        foreach (int delay in new[] { 120, 150 })
        {
            await Task.Delay(delay);
            if (stepId != _stepCounter) return;
            await CaptureTargetsAsync();
        }
    }

    /// <summary>A small nudge-and-return when there is no desktop in that direction.</summary>
    private async Task BounceAsync(int dir)
    {
        foreach (var o in _overlays) o.Prepare(ScreenCapture.Capture(o.Bounds), 0);
        await ShowOverlaysAsync();
        await AnimateAsync(260, t =>
        {
            double s = Math.Sin(Math.PI * t);
            foreach (var o in _overlays) o.SetPose(-dir * 8 * s, 0.15 * s);
        });
        HideOverlays();
    }

    private static double EaseInOutCubic(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private Task AnimateAsync(int durationMs, Action<double> apply)
    {
        var tcs = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        int frames = 0;
        void OnFrame(object? s, EventArgs e)
        {
            frames++;
            double t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / Math.Max(1, durationMs));
            apply(t);
            if (t >= 1.0)
            {
                _lastFps = frames / Math.Max(0.001, clock.Elapsed.TotalSeconds);
                CompositionTarget.Rendering -= OnFrame;
                tcs.TrySetResult();
            }
        }
        CompositionTarget.Rendering += OnFrame;
        return tcs.Task;
    }

    /// <summary>Wait until WPF has actually presented a couple of frames.</summary>
    private static Task NextFramesAsync(int frames)
    {
        var tcs = new TaskCompletionSource();
        int seen = 0;
        void OnFrame(object? s, EventArgs e)
        {
            if (++seen >= frames)
            {
                CompositionTarget.Rendering -= OnFrame;
                tcs.TrySetResult();
            }
        }
        CompositionTarget.Rendering += OnFrame;
        return tcs.Task;
    }

    private async Task ShowOverlaysAsync()
    {
        foreach (var o in _overlays)
        {
            if (!o.IsVisible) o.Show();
            o.PlaceOnMonitor();
        }
        await NextFramesAsync(2);
        // A window has to be shown before it can be pinned; pinning keeps it visible across the switch.
        foreach (var o in _overlays)
        {
            if (VDA.IsPinnedWindow(o.Hwnd) != 1 && VDA.PinWindow(o.Hwnd) < 0)
                Log.Write($"PinWindow failed for overlay {o.Hwnd}");
        }
    }

    private void HideOverlays()
    {
        foreach (var o in _overlays) if (o.IsVisible) o.Hide();
    }

    private void SyncMonitors()
    {
        if (_busy) { _dispatcher.BeginInvoke(SyncMonitors, DispatcherPriority.Background); return; }
        foreach (var o in _overlays) o.Close();
        _overlays.Clear();
        foreach (var screen in Screen.AllScreens)
        {
            var o = new CubeOverlay(screen.Bounds, _settings);
            new System.Windows.Interop.WindowInteropHelper(o).EnsureHandle();
            _overlays.Add(o);
        }
        LoadWallpaper();
        Log.Write($"Monitors: {string.Join(", ", _overlays.Select(o => o.Bounds.ToString()))}");
    }

    private void LoadWallpaper()
    {
        ImageSource? img = null;
        if (_settings.WallpaperBackground)
        {
            try
            {
                string? path = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                        @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(path);
                    bmp.DecodePixelWidth = 1920;
                    bmp.EndInit();
                    bmp.Freeze();
                    img = bmp;
                }
            }
            catch (Exception e) { Log.Write($"Wallpaper load failed: {e.Message}"); }
        }
        foreach (var o in _overlays) o.SetWallpaper(img);
    }

    /// <summary>
    /// Switching through the COM API doesn't always move keyboard focus, so activate
    /// the topmost real window on the new desktop (or the desktop itself).
    /// </summary>
    private void FocusTopWindow()
    {
        var overlayHandles = _overlays.Select(o => o.Hwnd).ToHashSet();
        var cls = new StringBuilder(256);
        for (IntPtr h = Win32.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = Win32.GetWindow(h, Win32.GW_HWNDNEXT))
        {
            if (overlayHandles.Contains(h) || !Win32.IsWindowVisible(h) || Win32.IsIconic(h) || Win32.IsCloaked(h)) continue;
            long ex = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE);
            if ((ex & (Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE | 0x8 /*TOPMOST*/)) != 0) continue;
            if (Win32.GetWindowTextLength(h) == 0) continue;
            cls.Clear();
            Win32.GetClassName(h, cls, cls.Capacity);
            string c = cls.ToString();
            if (c is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW") continue;
            if (VDA.IsWindowOnCurrentVirtualDesktop(h) != 1) continue;
            Activate(h);
            return;
        }
        Activate(Win32.GetShellWindow());
    }

    private static void Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || Win32.GetForegroundWindow() == hwnd) return;
        uint fgThread = Win32.GetWindowThreadProcessId(Win32.GetForegroundWindow(), out _);
        uint me = Win32.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && Win32.AttachThreadInput(me, fgThread, true);
        Win32.SetForegroundWindow(hwnd);
        if (attached) Win32.AttachThreadInput(me, fgThread, false);
    }
}
