using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Rect = System.Drawing.Rectangle;

namespace WinDesktopCube;

/// <summary>
/// One full-screen, topmost window per monitor that renders the cube.
///
/// Its window is created once, pinned to all virtual desktops, excluded from screen
/// capture, and never hidden. Between switches it's parked just outside the virtual
/// screen. That keeps WPF's swap chain, the GPU textures and the render thread alive
/// and warm. Hiding the window let Windows page those out, and the first switch after
/// some idle time stuttered.
///
/// Geometry: a face is <c>aspect</c> wide and 1 tall. The "cube" is a prism whose
/// cross-section is aspect x aspect. The front face sits at z = +aspect/2, and the
/// left and right neighbours sit at x = -/+aspect/2. Rotating the prism -90 degrees
/// around Y brings the right face to the front.
/// </summary>
internal sealed class CubeOverlay : Window
{
    public Rect Bounds { get; }
    public IntPtr Hwnd { get; private set; }
    public MonitorCapture Capture { get; }
    public bool OnScreen { get; private set; }

    private readonly PerspectiveCamera _camera = new();
    private readonly AxisAngleRotation3D _rotation = new(new Vector3D(0, 1, 0), 0);
    private readonly WriteableBitmap _frontBmp, _sideBmp;
    private readonly DiffuseMaterial _sideMaterial;
    private readonly GeometryModel3D _front, _left, _right;
    private readonly Image _background = new() { Stretch = Stretch.UniformToFill, Opacity = 0.45 };
    private readonly double _aspect;
    private readonly double _baseDistance;

    public CubeOverlay(Rect bounds, Settings settings)
    {
        Bounds = bounds;
        Capture = new MonitorCapture(bounds);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Black;
        Title = "WinDesktopCube overlay";
        Left = bounds.X; Top = bounds.Y; Width = bounds.Width; Height = bounds.Height; // corrected in SourceInitialized

        // Textures are allocated once and overwritten in place on every switch.
        _frontBmp = new WriteableBitmap(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgr32, null);
        _sideBmp = new WriteableBitmap(bounds.Width, bounds.Height, 96, 96, PixelFormats.Bgr32, null);
        var frontBrush = new ImageBrush(_frontBmp) { Stretch = Stretch.Fill };
        var sideBrush = new ImageBrush(_sideBmp) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(frontBrush, BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(sideBrush, BitmapScalingMode.HighQuality);
        _sideMaterial = new DiffuseMaterial(sideBrush);

        _front = new GeometryModel3D { Material = new DiffuseMaterial(frontBrush) };
        _left = new GeometryModel3D();
        _right = new GeometryModel3D();
        var faces = new Model3DGroup();
        faces.Children.Add(_front);
        faces.Children.Add(_left);
        faces.Children.Add(_right);
        faces.Transform = new RotateTransform3D(_rotation);

        var scene = new Model3DGroup();
        // Faces pointing at the camera get full brightness; faces turned 90 degrees away get about 60%.
        scene.Children.Add(new AmbientLight(Color.FromRgb(0x9A, 0x9A, 0x9A)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0x65, 0x65, 0x65), new Vector3D(0, 0, -1)));
        scene.Children.Add(faces);

        _aspect = (double)bounds.Width / bounds.Height;
        double h = _aspect / 2; // half the prism depth/width
        _front.Geometry = Quad(new(-h, -0.5, h), new(h, -0.5, h), new(h, 0.5, h), new(-h, 0.5, h));
        _right.Geometry = Quad(new(h, -0.5, h), new(h, -0.5, -h), new(h, 0.5, -h), new(h, 0.5, h));
        _left.Geometry = Quad(new(-h, -0.5, -h), new(-h, -0.5, h), new(-h, 0.5, h), new(-h, 0.5, -h));

        _camera.LookDirection = new Vector3D(0, 0, -1);
        _camera.UpDirection = new Vector3D(0, 1, 0);
        _camera.NearPlaneDistance = 0.01;
        _camera.FarPlaneDistance = 100;
        _camera.FieldOfView = settings.FieldOfView; // horizontal FOV in WPF
        _baseDistance = h / Math.Tan(settings.FieldOfView * Math.PI / 360.0);
        SetPose(0, 0);

        var viewport = new Viewport3D { Camera = _camera, ClipToBounds = true };
        viewport.Children.Add(new ModelVisual3D { Content = scene });

        var root = new Grid();
        root.Children.Add(_background);
        root.Children.Add(viewport);
        Content = root;

        SourceInitialized += (_, _) => OnHandleCreated();
        Closed += (_, _) => Capture.Dispose();
    }

    private void OnHandleCreated()
    {
        Hwnd = new WindowInteropHelper(this).Handle;
        long ex = Win32.GetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE);
        ex |= Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW;
        ex &= ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE, ex);
        // Keep ourselves out of BitBlt/Graphics Capture so we can photograph the desktop underneath.
        if (!Win32.SetWindowDisplayAffinity(Hwnd, Win32.WDA_EXCLUDEFROMCAPTURE))
            Log.Write("SetWindowDisplayAffinity(EXCLUDEFROMCAPTURE) failed");
        Park();
    }

    /// <summary>Move onto the monitor (same size, so WPF keeps its swap chain).</summary>
    public void PlaceOnMonitor()
    {
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, Win32.SWP_NOACTIVATE);
        OnScreen = true;
    }

    /// <summary>Move just past the right edge of the whole virtual screen, where no monitor can show it.</summary>
    public void Park()
    {
        var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
        Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, vs.Right + 64, vs.Top, Bounds.Width, Bounds.Height, Win32.SWP_NOACTIVATE);
        OnScreen = false;
    }

    public void SetWallpaper(ImageSource? wallpaper) => _background.Source = wallpaper;

    /// <summary>Quad from bottom-left, bottom-right, top-right, top-left (counter-clockwise as seen from outside).</summary>
    private static MeshGeometry3D Quad(Point3D bl, Point3D br, Point3D tr, Point3D tl)
    {
        var m = new MeshGeometry3D
        {
            Positions = new Point3DCollection { bl, br, tr, tl },
            TextureCoordinates = new PointCollection { new(0, 1), new(1, 1), new(1, 0), new(0, 0) },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 },
        };
        m.Freeze();
        return m;
    }

    /// <summary>Copy the latest <see cref="Capture"/> into the front face.</summary>
    public void LoadFrontFromCapture() =>
        _frontBmp.WritePixels(new Int32Rect(0, 0, Bounds.Width, Bounds.Height), Capture.Bits, Capture.BufferSize, Capture.Stride);

    /// <summary>Copy the latest <see cref="Capture"/> into the target (side) face.</summary>
    public void LoadSideFromCapture() =>
        _sideBmp.WritePixels(new Int32Rect(0, 0, Bounds.Width, Bounds.Height), Capture.Bits, Capture.BufferSize, Capture.Stride);

    /// <summary>Show the target face on the right (+1), the left (-1), or neither (0).</summary>
    public void SetDirection(int direction)
    {
        _right.Material = direction > 0 ? _sideMaterial : null;
        _left.Material = direction < 0 ? _sideMaterial : null;
    }

    /// <summary>Debug aid: render the overlay's current frame to a PNG (screenshots can't see it).</summary>
    public void SaveSnapshot(string path)
    {
        var root = (FrameworkElement)Content;
        var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    /// <param name="angle">Prism rotation around Y, in degrees.</param>
    /// <param name="zoom">How far to pull the camera back, in face heights.</param>
    public void SetPose(double angle, double zoom)
    {
        _rotation.Angle = angle;
        _camera.Position = new Point3D(0, 0, _aspect / 2 + _baseDistance + zoom);
    }
}
