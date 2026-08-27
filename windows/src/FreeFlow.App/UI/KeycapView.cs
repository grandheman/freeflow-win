using System;
using System.Windows;
using System.Windows.Controls;
using FreeFlow.Core.Shortcuts;

namespace FreeFlow.App.UI;

/// <summary>
/// Renders a shortcut binding as a row of physical keycaps.
/// </summary>
/// <remarks>
/// The app is driven entirely by held keys, so a shortcut is shown as the keys the
/// user actually presses rather than as a text string like "Ctrl+Shift+D". The same
/// control is used in the overlay, Settings, and setup, so a shortcut looks identical
/// wherever it appears.
/// </remarks>
public sealed class KeycapView : ItemsControl
{
    public static readonly DependencyProperty BindingProperty = DependencyProperty.Register(
        nameof(Binding), typeof(ShortcutBinding), typeof(KeycapView),
        new PropertyMetadata(null, OnBindingChanged));

    public ShortcutBinding? Binding
    {
        get => (ShortcutBinding?)GetValue(BindingProperty);
        set => SetValue(BindingProperty, value);
    }

    public KeycapView()
    {
        ItemsPanel = BuildHorizontalPanel();
        ItemTemplate = BuildKeycapTemplate();
    }

    private static void OnBindingChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not KeycapView view) return;

        var binding = args.NewValue as ShortcutBinding;
        view.ItemsSource = binding is null || binding.IsDisabled
            ? new[] { "Disabled" }
            : KeyLabels(binding);
    }

    /// <summary>Modifier keys first, then the primary key, matching physical order.</summary>
    private static string[] KeyLabels(ShortcutBinding binding)
    {
        var modifiers = binding.ModifierDisplayNames;
        var primary = binding.Kind == ShortcutBindingKind.ModifierKey
            ? VirtualKeys.ExactModifierDisplayLabel(binding.KeyCode) ?? binding.KeyDisplay
            : binding.KeyDisplay;

        var labels = new string[modifiers.Count + 1];
        for (var index = 0; index < modifiers.Count; index++) labels[index] = modifiers[index];
        labels[^1] = primary;

        return labels;
    }

    private static ItemsPanelTemplate BuildHorizontalPanel()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        return new ItemsPanelTemplate(panel);
    }

    private static DataTemplate BuildKeycapTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(FrameworkElement.StyleProperty, new DynamicResourceExtension("Keycap"));
        border.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0));

        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(FrameworkElement.StyleProperty, new DynamicResourceExtension("KeycapText"));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());

        border.AppendChild(text);

        return new DataTemplate { VisualTree = border };
    }
}
