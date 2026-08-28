using System.Drawing;
using System.IO;

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
}
