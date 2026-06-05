using System.Windows;
using System.Windows.Media;

namespace Mystral.Views;

internal sealed record LyricWaitIndicator(
    TimeSpan Start,
    TimeSpan End,
    FrameworkElement Element,
    IReadOnlyList<SolidColorBrush> DotBrushes,
    int LineIndex = -1);
