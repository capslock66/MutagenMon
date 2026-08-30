using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MutagenMon.App;

/// <summary>Converts a <see cref="MutagenMon.Core.Status.SessionSummaryRow"/>'s
/// IconKey (a bitmap-agnostic string from Core, e.g. "green-sync") to the
/// actual <see cref="ImageSource"/> for the status view's per-session grid
/// icon (FR-8.1). Declared as a XAML resource with a parameterless
/// constructor, so <see cref="Cache"/> is wired in from code-behind
/// (<see cref="StatusWindow"/>) once the app's <see cref="IconImageCache"/>
/// is available.</summary>
public sealed class IconKeyToImageSourceConverter : IValueConverter
{
    public IconImageCache? Cache { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string iconKey && !string.IsNullOrEmpty(iconKey) ? Cache?.GetImageSource(iconKey) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
