# WinDesktopCube

A 3D cube transition for Windows 11 virtual desktops, inspired by the GNOME
[Desktop Cube](https://github.com/Schneegans/Desktop-Cube) extension.

Press **Ctrl+Win+Left/Right** and your desktops turn like the faces of a cube
instead of Windows' flat slide.

## How it works

Windows has no API for a custom desktop-switch animation, so we stage our own:

1. A low-level keyboard hook swallows Ctrl+Win+Left/Right, so Windows never starts its own animation.
2. We photograph each monitor (GDI BitBlt).
3. A topmost overlay window per monitor shows that photo as the cube's front face. The overlay is
   - **pinned to all desktops** (`PinWindow`), so it stays on screen through the switch, and
   - **excluded from capture** (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`), so later captures see *through* it.
4. Behind the overlay we switch desktops through the undocumented virtual-desktop COM API
   (`GoToDesktopNumber`), which switches instantly with no Windows animation.
5. We photograph the new desktop live (the overlay is invisible to capture) and put it on the side face.
   We re-capture twice during the turn, because Explorer swaps per-desktop wallpapers a little late.
6. WPF 3D turns the prism 90° while the camera pulls back and returns. Then the overlay hides and the
   real desktop is underneath, matching the last frame.

| File | Role |
|---|---|
| `src/KeyboardHook.cs` | Takes over Ctrl+Win+arrows. Injects a dummy key so releasing Win doesn't open Start. |
| `src/CubeOverlay.cs` | Per-monitor overlay window: WPF `Viewport3D` scene, camera, faces. |
| `src/CubeController.cs` | Switch sequence, queuing for held keys, wrap-around, edge bounce, focus fix-up. |
| `src/ScreenCapture.cs` | BitBlt → frozen `BitmapSource`. |
| `src/Native.cs` | P/Invoke for user32/gdi32/dwmapi and VirtualDesktopAccessor.dll. |
| `lib/VirtualDesktopAccessor.dll` | Built from [Ciantic/VirtualDesktopAccessor](https://github.com/Ciantic/VirtualDesktopAccessor) (MIT). |

## Build & run

Requires the .NET 9 SDK. Rust is only needed to rebuild the native DLL.

```powershell
cd src
dotnet build -c Release
.\bin\Release\net9.0-windows\win-x64\WinDesktopCube.exe
```

A cube icon appears in the tray (rotate left/right, wrap-around, speed, settings, exit).

Debug flags: `--demo` rotates right and back once on startup. `--snap` saves PNGs of the overlay mid-turn
to `%APPDATA%\WinDesktopCube` (normal screenshots can't see the overlay).

### Rebuilding VirtualDesktopAccessor.dll

The virtual-desktop COM interfaces are undocumented and their GUIDs change between Windows builds.
If a Windows update breaks switching, update and rebuild:

```powershell
git clone https://github.com/Ciantic/VirtualDesktopAccessor deps/VirtualDesktopAccessor
cargo build --release --manifest-path deps/VirtualDesktopAccessor/dll/Cargo.toml
copy deps\VirtualDesktopAccessor\target\release\VirtualDesktopAccessor.dll lib\
```

## Settings

`%APPDATA%\WinDesktopCube\settings.json`:

| Key | Default | Meaning |
|---|---|---|
| `AnimationMs` | 450 | Duration of one 90° turn |
| `WrapAround` | true | Last desktop → first (Windows itself doesn't wrap) |
| `ZoomOut` | 0.45 | How far the camera pulls back mid-turn (× screen width) |
| `FieldOfView` | 45 | Horizontal FOV. Lower = flatter perspective |
| `CaptureDelayMs` | 50 | Wait after the hidden switch before photographing the new desktop |
| `WallpaperBackground` | true | Dimmed wallpaper behind the cube instead of black |

Settings are read at startup. Restart the app after editing the file.

## Known limitations

- Only switches started by our hotkeys (or the tray) get the cube. Task View clicks and touchpad
  4-finger swipes still use Windows' animation.
- Windows 11 shows its own "Desktop N" name flyout after a switch. It can appear on the cube face.
- Content that is hidden from capture (DRM video, some password managers) appears black on the faces.
- Elevated (admin) windows don't receive our hook's key events while focused, so Windows' native
  switch runs there. Running WinDesktopCube elevated fixes this.

## Roadmap ideas (from the GNOME original)

- Drag-to-rotate: click-drag on the desktop or taskbar to spin the cube by hand.
- Touchpad gestures via raw input from precision touchpads (needs Windows' 4-finger gesture set to "Nothing").
- A "cube overview" showing all desktops at once, with clicks to jump.
- Per-desktop wallpaper as the background, or a 360° panorama skybox.
- Multi-step jumps (Win+Ctrl+number) as a longer single spin.
- Start with Windows (registry Run key) and a proper settings window.
