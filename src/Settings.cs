using System;
using System.IO;
using System.Text.Json;

namespace WinDesktopCube;

internal sealed class Settings
{
    /// <summary>Duration of one 90-degree rotation.</summary>
    public int AnimationMs { get; set; } = 450;

    /// <summary>Go from the last desktop to the first (and back). Windows doesn't do this natively.</summary>
    public bool WrapAround { get; set; } = true;

    /// <summary>How far the camera pulls back mid-rotation, as a fraction of the screen width.</summary>
    public double ZoomOut { get; set; } = 0.45;

    /// <summary>Horizontal field of view of the camera, in degrees. Lower values flatten the perspective.</summary>
    public double FieldOfView { get; set; } = 45;

    /// <summary>How long to wait after the (hidden) desktop switch before photographing the new desktop.</summary>
    public int CaptureDelayMs { get; set; } = 50;

    /// <summary>Show the dimmed wallpaper behind the cube instead of black.</summary>
    public bool WallpaperBackground { get; set; } = true;

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinDesktopCube");
    public static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception e) { Log.Write($"Settings load failed: {e.Message}"); }
        var s = new Settings();
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) { Log.Write($"Settings save failed: {e.Message}"); }
    }
}

internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(Path.Combine(Settings.Dir, "log.txt"), $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }
}
