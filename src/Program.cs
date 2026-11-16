using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace WinDesktopCube;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\WinDesktopCube", out bool first);
        if (!first) return 0;

        var app = new Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var settings = Settings.Load();
        Log.Write($"Starting. Desktops={VDA.GetDesktopCount()} current={VDA.GetCurrentDesktopNumber()}");

        CubeController.DebugSnapshots = Array.IndexOf(args, "--snap") >= 0;
        var controller = new CubeController(app.Dispatcher, settings);
        using var hook = new KeyboardHook();
        hook.SwitchRequested += controller.Request;

        using var tray = BuildTray(app, controller, settings);

        // --demo: rotate right then back left, to try the effect without hotkeys.
        if (Array.IndexOf(args, "--demo") >= 0)
        {
            app.Dispatcher.BeginInvoke(async () =>
            {
                await System.Threading.Tasks.Task.Delay(800);
                controller.Request(+1);
                await System.Threading.Tasks.Task.Delay(2000);
                controller.Request(-1);
            });
        }

        app.Run();
        return 0;
    }

    private static NotifyIcon BuildTray(Application app, CubeController controller, Settings settings)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("WinDesktopCube") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Rotate left  (Ctrl+Win+←)", null, (_, _) => controller.Request(-1));
        menu.Items.Add("Rotate right (Ctrl+Win+→)", null, (_, _) => controller.Request(+1));
        menu.Items.Add(new ToolStripSeparator());

        var wrap = new ToolStripMenuItem("Wrap around") { Checked = settings.WrapAround, CheckOnClick = true };
        wrap.CheckedChanged += (_, _) => { settings.WrapAround = wrap.Checked; settings.Save(); };
        menu.Items.Add(wrap);

        var speed = new ToolStripMenuItem("Animation speed");
        foreach (var (name, ms) in new[] { ("Fast", 300), ("Normal", 450), ("Slow", 700), ("Very slow (debug)", 2000) })
        {
            var item = new ToolStripMenuItem(name) { Checked = settings.AnimationMs == ms, Tag = ms };
            item.Click += (_, _) =>
            {
                settings.AnimationMs = ms;
                settings.Save();
                foreach (ToolStripMenuItem i in speed.DropDownItems) i.Checked = (int)i.Tag! == ms;
            };
            speed.DropDownItems.Add(item);
        }
        menu.Items.Add(speed);
        menu.Items.Add("Open settings file", null, (_, _) =>
            Process.Start(new ProcessStartInfo(Settings.FilePath) { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => app.Shutdown());

        return new NotifyIcon
        {
            Icon = DrawCubeIcon(),
            Text = "WinDesktopCube: Ctrl+Win+Left/Right",
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    /// <summary>A small isometric cube for the tray icon, drawn in code so there are no asset files.</summary>
    private static Icon DrawCubeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var top = new[] { new PointF(16, 3), new PointF(29, 10), new PointF(16, 17), new PointF(3, 10) };
            var left = new[] { new PointF(3, 10), new PointF(16, 17), new PointF(16, 30), new PointF(3, 23) };
            var right = new[] { new PointF(16, 17), new PointF(29, 10), new PointF(29, 23), new PointF(16, 30) };
            using var b1 = new SolidBrush(Color.FromArgb(0x7F, 0xC8, 0xFF));
            using var b2 = new SolidBrush(Color.FromArgb(0x2F, 0x86, 0xD6));
            using var b3 = new SolidBrush(Color.FromArgb(0x1A, 0x5A, 0x9E));
            g.FillPolygon(b1, top);
            g.FillPolygon(b2, left);
            g.FillPolygon(b3, right);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
