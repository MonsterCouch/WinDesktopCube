using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Screen = System.Windows.Forms.Screen;

namespace WinDesktopCube;

/// <summary>
/// Runs a desktop switch:
///  1. photograph the current desktop on each monitor (often done early, see <see cref="Prewarm"/>)
///  2. move each monitor's pinned overlay on screen, showing that photo as the cube's front face
///  3. switch the virtual desktop underneath (no Windows animation; the overlay hides it)
///  4. photograph the new desktop (the overlay is excluded from capture) and use it as the side face
///  5. rotate the cube 90 degrees, then park the overlay off-screen to reveal the real desktop
/// </summary>
internal sealed class CubeController
{
    public static bool DebugSnapshots;
    public static bool DisablePrewarm;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private readonly Dispatcher _dispatcher;
    private readonly Settings _settings;
    private readonly List<CubeOverlay> _overlays = new();
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private bool _busy;
    private int _pendingSteps;
    private int _stepCounter;
    private string _lastFrameStats = "";
    private double _lastActivityMs;

    private Task? _prewarm;
    private double _prewarmedAtMs = double.NegativeInfinity;
    private int _prewarmedDesktop = -1;

    public CubeController(Dispatcher dispatcher, Settings settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _dispatcher.BeginInvoke(SyncMonitors);
        SystemEvents.DisplaySettingsChanged += (_, _) => _dispatcher.BeginInvoke(SyncMonitors);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.Desktop) _dispatcher.BeginInvoke(LoadWallpaper);
        };
    }

    private static double NowMs => Clock.Elapsed.TotalMilliseconds;
    private bool Capturing => _busy || _prewarm is { IsCompleted: false };

    /// <summary>Safe to call from any thread, including the keyboard hook callback.</summary>
    public void Request(int direction) => _dispatcher.BeginInvoke(() =>
    {
        _pendingSteps += direction;
        if (!_busy) _ = RunAsync();
    });

    /// <summary>
    /// Called when Ctrl+Win goes down, typically 100-300ms before the arrow key.
    /// That's enough time to photograph the current desktop and push a frame through
    /// the GPU, so memory and clocks that idled down are back up before the cube
    /// has to move. Safe to call from any thread.
    /// </summary>
    public void Prewarm() => _dispatcher.BeginInvoke(() =>
    {
        if (DisablePrewarm || Capturing || _overlays.Count == 0) return;
        _prewarm = PrewarmAsync();
    });

    private async Task PrewarmAsync()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            int desktop = VDA.GetCurrentDesktopNumber();
            await GrabAsync(front: true);
            foreach (var o in _overlays) { o.SetDirection(+1); o.SetPose(-0.01, 0); } // off-screen, but forces a real render
            await NextFramesAsync(2);
            foreach (var o in _overlays) o.SetPose(0, 0);
            _prewarmedAtMs = NowMs;
            _prewarmedDesktop = desktop;
            Log.Write($"Prewarmed in {sw.ElapsedMilliseconds}ms");
        }
        catch (Exception e) { Log.Write($"Prewarm failed: {e.Message}"); }
    }

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
            ParkOverlays();
            _busy = false;
            _lastActivityMs = NowMs;
        }
    }

    private async Task StepAsync(int dir)
    {
        if (_prewarm is { IsCompleted: false }) await _prewarm;

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
        double idleSec = (NowMs - _lastActivityMs) / 1000;

        // Reuse the Ctrl+Win prewarm photo if it's recent and of this desktop. Otherwise
        // (no prewarm, or a chained step while the overlay is up) take a fresh one.
        bool reused = !_overlays[0].OnScreen && current == _prewarmedDesktop && NowMs - _prewarmedAtMs < 1500;
        _prewarmedAtMs = double.NegativeInfinity;
        if (!reused) await GrabAsync(front: true);
        foreach (var o in _overlays) { o.SetDirection(dir); o.SetPose(0, 0); }
        await ShowOverlaysAsync();
        long tShown = sw.ElapsedMilliseconds;

        if (VDA.GoToDesktopNumber(target) < 0) Log.Write($"GoToDesktopNumber({target}) failed");
        await Task.Delay(_settings.CaptureDelayMs);
        await GrabAsync(front: false);
        long tCaptured = sw.ElapsedMilliseconds;

        // Explorer repaints the per-desktop wallpaper (and some apps their content) a little
        // after the switch, so refresh the target face a couple of times while the cube turns.
        int stepId = ++_stepCounter;
        _ = RefreshTargetsAsync(stepId);

        double aspect = (double)_overlays[0].Bounds.Width / _overlays[0].Bounds.Height;
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

        Log.Write($"Switched {current}->{target} (idle {idleSec:F0}s, prewarm {(reused ? "used" : "missed")}): " +
                  $"overlay up {tShown}ms, target captured {tCaptured}ms, done {sw.ElapsedMilliseconds}ms; {_lastFrameStats}");

        if (_pendingSteps == 0)
        {
            ParkOverlays();
            FocusTopWindow();
        }
    }

    /// <summary>A small nudge-and-return when there is no desktop in that direction.</summary>
    private async Task BounceAsync(int dir)
    {
        await GrabAsync(front: true);
        foreach (var o in _overlays) { o.SetDirection(0); o.SetPose(0, 0); }
        await ShowOverlaysAsync();
        await AnimateAsync(260, t =>
        {
            double s = Math.Sin(Math.PI * t);
            foreach (var o in _overlays) o.SetPose(-dir * 8 * s, 0.15 * s);
        });
        ParkOverlays();
    }

    /// <summary>Photograph every monitor on a worker thread, then upload into the front or side texture.</summary>
    private async Task GrabAsync(bool front)
    {
        await _captureLock.WaitAsync();
        try
        {
            var overlays = _overlays.ToArray();
            await Task.Run(() => { foreach (var o in overlays) o.Capture.Grab(); });
            foreach (var o in overlays)
            {
                if (front) o.LoadFrontFromCapture();
                else o.LoadSideFromCapture();
            }
        }
        finally { _captureLock.Release(); }
    }

    private async Task RefreshTargetsAsync(int stepId)
    {
        foreach (int delay in new[] { 120, 150 })
        {
            await Task.Delay(delay);
            if (stepId != _stepCounter) return;
            await GrabAsync(front: false);
        }
    }

    private static double EaseInOutCubic(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>Drive <paramref name="apply"/> once per WPF frame and record frame-time stats.</summary>
    private Task AnimateAsync(int durationMs, Action<double> apply)
    {
        var tcs = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        int frames = 0, slow = 0;
        double last = 0, worst = 0, firstFrame = -1;
        void OnFrame(object? s, EventArgs e)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (frames == 0) firstFrame = now;
            else
            {
                double gap = now - last;
                worst = Math.Max(worst, gap);
                if (gap > 25) slow++;
            }
            last = now;
            frames++;

            double t = Math.Min(1.0, now / Math.Max(1, durationMs));
            apply(t);
            if (t >= 1.0)
            {
                CompositionTarget.Rendering -= OnFrame;
                _lastFrameStats = $"{frames / Math.Max(0.001, now / 1000):F0} fps, worst frame {worst:F0}ms, " +
                                  $"{slow} frames >25ms, first frame after {firstFrame:F0}ms";
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
        foreach (var o in _overlays) if (!o.OnScreen) o.PlaceOnMonitor();
        await NextFramesAsync(2);
        EnsurePinned(); // cheap; covers Explorer restarts, which forget pins
    }

    private void EnsurePinned()
    {
        foreach (var o in _overlays)
        {
            if (VDA.IsPinnedWindow(o.Hwnd) != 1 && VDA.PinWindow(o.Hwnd) < 0)
                Log.Write($"PinWindow failed for overlay {o.Hwnd}");
        }
    }

    private void ParkOverlays()
    {
        foreach (var o in _overlays) if (o.OnScreen) o.Park();
    }

    private async void SyncMonitors()
    {
        if (Capturing)
        {
            await Task.Delay(500);
            SyncMonitors();
            return;
        }
        _busy = true; // keep switches out while we rebuild
        try
        {
            foreach (var o in _overlays) o.Close();
            _overlays.Clear();
            foreach (var screen in Screen.AllScreens)
            {
                var o = new CubeOverlay(screen.Bounds, _settings);
                o.Show(); // shown once and never hidden: it lives parked off-screen
                o.Park();
                _overlays.Add(o);
            }
            LoadWallpaper();
            await NextFramesAsync(2);
            EnsurePinned();

            // Run one invisible rotation so every code path is JIT-compiled and the
            // GPU resources exist before the first real switch.
            await GrabAsync(front: true);
            await GrabAsync(front: false);
            foreach (var o in _overlays) o.SetDirection(+1);
            await AnimateAsync(150, t => { foreach (var o in _overlays) o.SetPose(-90 * t, t); });
            foreach (var o in _overlays) o.SetPose(0, 0);

            Log.Write($"Monitors: {string.Join(", ", _overlays.Select(o => o.Bounds.ToString()))} (warm-up {_lastFrameStats})");
        }
        catch (Exception e) { Log.Write($"Monitor setup failed: {e}"); }
        finally
        {
            _busy = false;
            _lastActivityMs = NowMs;
            if (_pendingSteps != 0) _ = RunAsync(); // a hotkey arrived during setup
        }
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
