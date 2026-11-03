using System.Diagnostics;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Curtain.App.Controls;

public sealed class ScrollAwareSettingsExpander : SettingsExpander
{
    private ScrollViewer? _scroll;
    private long _started;
    private double _from;
    private double _to;

    public ScrollAwareSettingsExpander()
    {
        Collapsed += OnCollapsed;
        Expanded += (_, _) => StopScrolling();
        Unloaded += (_, _) => StopScrolling();
    }

    private void OnCollapsed(object? sender, EventArgs args)
    {
        StopScrolling();
        // The native template removes this area after its 167 ms collapse motion.
        // Start correcting the future scroll boundary now, rather than after that removal.
        var content = FindContentClip(this);
        var scroll = FindScrollViewer(this);
        if (content is null || scroll is null) return;
        double limit = Math.Max(0, scroll.ScrollableHeight - content.ActualHeight * scroll.ZoomFactor);
        if (scroll.VerticalOffset <= limit + 0.5) return;
        _scroll = scroll;
        _from = scroll.VerticalOffset;
        _to = limit;
        if (!new global::Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            scroll.ChangeView(null, _to, null, true);
            _scroll = null;
            return;
        }
        _started = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object args)
    {
        if (_scroll is null) return;
        double progress = Math.Clamp(Stopwatch.GetElapsedTime(_started).TotalMilliseconds / 167, 0, 1);
        double eased = 1 - Math.Pow(1 - progress, 3);
        _scroll.ChangeView(null, _from + (_to - _from) * eased, null, true);
        if (progress >= 1) StopScrolling();
    }

    private void StopScrolling()
    {
        CompositionTarget.Rendering -= OnRendering;
        _scroll = null;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer scroll) return scroll;
        return null;
    }

    private static FrameworkElement? FindContentClip(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: "ExpanderContentClip" } clip) return clip;
            if (FindContentClip(child) is { } nested) return nested;
        }
        return null;
    }
}
