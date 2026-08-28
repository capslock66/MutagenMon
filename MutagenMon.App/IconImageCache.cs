using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MutagenMon.App;

/// <summary>Loads/caches a <see cref="Icon"/> per icon key from Assets/Icons/*.ico.
/// Loading a real .ico directly gives H.NotifyIcon's TaskbarIcon.Icon a native
/// Windows icon handle as-is. The alternative — TaskbarIcon.IconSource fed
/// with a PNG-backed BitmapImage — resolves by converting that ImageSource to
/// an Icon internally (H.NotifyIcon.Wpf's StreamExtensions.ToSmallIcon), which
/// throws "Argument 'picture' must be a picture that can be used as a Icon."
/// for PNGs that GDI+'s icon conversion rejects. Using .ico end to end avoids
/// that conversion path altogether.
///
/// <see cref="Get"/> returns a clone of the cached master icon rather than the
/// master itself: H.NotifyIcon's TaskbarIcon.Icon setter takes ownership of
/// whatever Icon it's given and disposes the previous one when it's replaced
/// (it assumes each assigned icon is a one-shot object). Handing out the same
/// cached instance across multiple assignments meant a later re-display of an
/// already-superseded icon key reused an instance H.NotifyIcon had already
/// disposed, throwing ObjectDisposedException from TaskbarIcon.UpdateIcon.
/// Cloning keeps the master alive and independent of whatever H.NotifyIcon
/// does with the clone it was handed.</summary>
public sealed class IconImageCache
{
    private readonly string _iconsDirectory;
    private readonly Dictionary<string, Icon> _cache = new();
    private readonly Dictionary<string, ImageSource> _imageSourceCache = new();

    public IconImageCache(string iconsDirectory)
    {
        _iconsDirectory = iconsDirectory;
    }

    public Icon Get(string iconKey)
    {
        if (!_cache.TryGetValue(iconKey, out var master))
        {
            var path = Path.Combine(_iconsDirectory, iconKey + ".ico");
            master = new Icon(path);
            _cache[iconKey] = master;
        }

        return (Icon)master.Clone();
    }

    /// <summary>WPF-side counterpart of <see cref="Get"/>, for controls
    /// (e.g. an <c>Image</c>) that need an <see cref="ImageSource"/> rather
    /// than a GDI <see cref="Icon"/>. Unlike <see cref="Get"/>, the returned
    /// value IS the cached, frozen instance, shared across every caller —
    /// safe because a frozen ImageSource is immutable, unlike an
    /// H.NotifyIcon-owned Icon which the caller must not share (see class
    /// remarks).</summary>
    public ImageSource GetImageSource(string iconKey)
    {
        if (_imageSourceCache.TryGetValue(iconKey, out var cached))
            return cached;

        using var icon = Get(iconKey);
        var imageSource = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        imageSource.Freeze();
        _imageSourceCache[iconKey] = imageSource;
        return imageSource;
    }
}
