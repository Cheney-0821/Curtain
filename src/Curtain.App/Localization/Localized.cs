using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Curtain.App.Localization;

public static class Localized
{
    private sealed class Entry
    {
        internal readonly Dictionary<DependencyProperty, object?[]> Arguments = new();
    }

    private static readonly ConditionalWeakTable<DependencyObject, Entry> Entries = new();
    private static readonly List<WeakReference<DependencyObject>> Targets = new();

    public static readonly DependencyProperty TextKeyProperty = Register("TextKey");
    public static readonly DependencyProperty ContentKeyProperty = Register("ContentKey");
    public static readonly DependencyProperty HeaderKeyProperty = Register("HeaderKey");
    public static readonly DependencyProperty TitleKeyProperty = Register("TitleKey");
    public static readonly DependencyProperty ToolTipKeyProperty = Register("ToolTipKey");
    public static readonly DependencyProperty AutomationNameKeyProperty = Register("AutomationNameKey");

    public static string? GetTextKey(DependencyObject target) => (string?)target.GetValue(TextKeyProperty);
    public static void SetTextKey(DependencyObject target, string? value) => target.SetValue(TextKeyProperty, value);
    public static string? GetContentKey(DependencyObject target) => (string?)target.GetValue(ContentKeyProperty);
    public static void SetContentKey(DependencyObject target, string? value) => target.SetValue(ContentKeyProperty, value);
    public static string? GetHeaderKey(DependencyObject target) => (string?)target.GetValue(HeaderKeyProperty);
    public static void SetHeaderKey(DependencyObject target, string? value) => target.SetValue(HeaderKeyProperty, value);
    public static string? GetTitleKey(DependencyObject target) => (string?)target.GetValue(TitleKeyProperty);
    public static void SetTitleKey(DependencyObject target, string? value) => target.SetValue(TitleKeyProperty, value);
    public static string? GetToolTipKey(DependencyObject target) => (string?)target.GetValue(ToolTipKeyProperty);
    public static void SetToolTipKey(DependencyObject target, string? value) => target.SetValue(ToolTipKeyProperty, value);
    public static string? GetAutomationNameKey(DependencyObject target) => (string?)target.GetValue(AutomationNameKeyProperty);
    public static void SetAutomationNameKey(DependencyObject target, string? value) => target.SetValue(AutomationNameKeyProperty, value);

    public static void SetText(TextBlock target, string key, params object?[] arguments) => Set(target, TextKeyProperty, key, arguments);
    public static void SetContent(DependencyObject target, string key, params object?[] arguments) => Set(target, ContentKeyProperty, key, arguments);
    public static void SetHeader(DependencyObject target, string key, params object?[] arguments) => Set(target, HeaderKeyProperty, key, arguments);
    public static void SetTitle(DependencyObject target, string key, params object?[] arguments) => Set(target, TitleKeyProperty, key, arguments);
    public static void SetAutomationName(DependencyObject target, string key, params object?[] arguments) =>
        Set(target, AutomationNameKeyProperty, key, arguments);

    public static void RefreshAll()
    {
        for (int index = Targets.Count - 1; index >= 0; index--)
        {
            if (Targets[index].TryGetTarget(out var target)) Apply(target);
            else Targets.RemoveAt(index);
        }
    }

    private static DependencyProperty Register(string name) => DependencyProperty.RegisterAttached(
        name, typeof(string), typeof(Localized), new PropertyMetadata(null, OnKeyChanged));

    private static void OnKeyChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not string) return;
        if (!Entries.TryGetValue(target, out _))
        {
            Entries.Add(target, new Entry());
            Targets.Add(new WeakReference<DependencyObject>(target));
        }
        Apply(target);
    }

    private static void Set(DependencyObject target, DependencyProperty property, string key, object?[] arguments)
    {
        if (!Entries.TryGetValue(target, out var entry))
        {
            entry = new Entry();
            Entries.Add(target, entry);
            Targets.Add(new WeakReference<DependencyObject>(target));
        }
        entry.Arguments[property] = arguments;
        target.SetValue(property, key);
        Apply(target);
    }

    private static void Apply(DependencyObject target)
    {
        if (!Entries.TryGetValue(target, out var entry)) return;
        ApplyKey(target, entry, TextKeyProperty, SetTextValue);
        ApplyKey(target, entry, ContentKeyProperty, SetContentValue);
        ApplyKey(target, entry, HeaderKeyProperty, SetHeaderValue);
        ApplyKey(target, entry, TitleKeyProperty, SetTitleValue);
        ApplyKey(target, entry, ToolTipKeyProperty, SetToolTipValue);
        ApplyKey(target, entry, AutomationNameKeyProperty, SetAutomationNameValue);
    }

    private static void ApplyKey(DependencyObject target, Entry entry, DependencyProperty property,
        Action<DependencyObject, string> apply)
    {
        if (target.GetValue(property) is not string key) return;
        entry.Arguments.TryGetValue(property, out var arguments);
        apply(target, LocalizationService.Get(key, arguments ?? Array.Empty<object?>()));
    }

    private static void SetTextValue(DependencyObject target, string value)
    {
        if (target is TextBlock textBlock) textBlock.Text = value;
        else if (target is TextBox textBox) textBox.Text = value;
    }

    private static void SetContentValue(DependencyObject target, string value)
    {
        if (target is MenuFlyoutItem flyoutItem) flyoutItem.Text = value;
        else if (target is ContentControl contentControl) contentControl.Content = value;
    }

    private static void SetHeaderValue(DependencyObject target, string value)
    {
        if (target is ToggleSwitch toggleSwitch) toggleSwitch.Header = value;
        else if (target is ComboBox comboBox) comboBox.Header = value;
    }

    private static void SetTitleValue(DependencyObject target, string value)
    {
        if (target is MenuBarItem menuBarItem) menuBarItem.Title = value;
    }

    private static void SetToolTipValue(DependencyObject target, string value)
    {
        if (target is FrameworkElement element) ToolTipService.SetToolTip(element, value);
    }

    private static void SetAutomationNameValue(DependencyObject target, string value) =>
        AutomationProperties.SetName(target, value);
}
