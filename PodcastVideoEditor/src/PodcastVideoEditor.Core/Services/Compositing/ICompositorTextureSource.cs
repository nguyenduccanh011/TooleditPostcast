#nullable enable
using SkiaSharp;

namespace PodcastVideoEditor.Core.Services.Compositing;

/// <summary>
/// Supplies decoded source images (as SkiaSharp <see cref="SKImage"/>) to the renderer.
/// Implementations are expected to decode once and cache; when drawn onto a GPU-backed
/// canvas, Skia uploads and caches the texture internally, so a single <see cref="SKImage"/>
/// per source is enough for a fully GPU-accelerated composite.
/// </summary>
public interface ICompositorTextureSource
{
    /// <summary>
    /// Returns the image for the given layer at the given scene time, or null if unavailable.
    /// For still images <paramref name="timeSeconds"/> is ignored; for video it selects the frame.
    /// </summary>
    SKImage? GetImage(CompositorLayer layer, double timeSeconds);
}
