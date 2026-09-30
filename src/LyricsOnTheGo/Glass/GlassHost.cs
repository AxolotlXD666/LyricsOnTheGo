using System;
using System.Numerics;
using LyricsOnTheGo.Interop;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace LyricsOnTheGo.Glass;

/// <summary>
/// Builds the visual tree for the tint window: a colour tint clipped to rounded corners.
/// The system-blurred HostBackdrop was removed locally (see Initialize), so this window
/// is now a plain transparent overlay whose only paint is the user's background colour.
/// </summary>
public sealed class GlassHost
{
    private Compositor _compositor = null!;
    private DesktopWindowTarget _target = null!;
    private CompositionRoundedRectangleGeometry _clipGeometry = null!;
    private SpriteVisual _tint = null!;
    private CompositionColorBrush _tintBrush = null!;

    public void Initialize(IntPtr hwnd, int width, int height)
    {
        _compositor = new Compositor();
        _target = CompositionInterop.CreateDesktopWindowTarget(_compositor, hwnd, isTopmost: true);

        var root = _compositor.CreateContainerVisual();
        root.RelativeSizeAdjustment = new Vector2(1f, 1f);
        _target.Root = root;

        // Rounded-corner clip — composition-level, so it works on Windows 10 AND 11
        _clipGeometry = _compositor.CreateRoundedRectangleGeometry();
        _clipGeometry.CornerRadius = new Vector2(12f, 12f);
        _clipGeometry.Size = new Vector2(width, height);
        root.Clip = _compositor.CreateGeometricClip(_clipGeometry);

        // LOCAL CHANGE (2026-09-29, at the owner's request): the host backdrop (acrylic blur of
        // whatever is behind the window) is gone, so the window is no longer a frosted panel.
        // The only thing painted now is the user's tint, whose opacity is the "background
        // opacity" setting - 0 means the overlay is fully transparent, i.e. only the lyrics.
        // To get the blur back, restore the CreateHostBackdropBrush block from git history.

        // Tint layer (user's bgcolor @ bgopacity). Updated live from settings.
        _tintBrush = _compositor.CreateColorBrush(Color.FromArgb(255, 0x08, 0x08, 0x08));
        _tint = _compositor.CreateSpriteVisual();
        _tint.RelativeSizeAdjustment = new Vector2(1f, 1f);
        _tint.Brush = _tintBrush;
        // LOCAL CHANGE (2026-09-29): start fully transparent. This used to be a hardcoded 0.35,
        // so a launch with background opacity 0 showed a 35% panel until something pushed the
        // saved setting in (the owner had to nudge the slider to make it right). MainWindow now
        // pushes the setting right after the glass starts and again on its timer.
        _tint.Opacity = 0f;
        root.Children.InsertAtTop(_tint);
    }

    public void Resize(int width, int height)
    {
        if (_clipGeometry != null)
            _clipGeometry.Size = new Vector2(width, height);
    }

    /// <summary>Live-updates the tint colour (RGB) and opacity (0–1). Must run on the glass thread.</summary>
    public void UpdateTint(byte r, byte g, byte b, float opacity)
    {
        if (_tintBrush is null || _tint is null)
            return;
        _tintBrush.Color = Color.FromArgb(255, r, g, b);
        _tint.Opacity = Math.Clamp(opacity, 0f, 1f);
    }
}
