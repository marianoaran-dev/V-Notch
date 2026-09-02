using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Models;

namespace VNotch;

public partial class MainWindow
{
    private static readonly (string Key, string Label)[] DisplayPresetSlots =
    [
        ("day", "Day"),
        ("night", "Night"),
        ("custom1", "Custom 1"),
        ("custom2", "Custom 2"),
        ("custom3", "Custom 3")
    ];

    private readonly Dictionary<string, Action<bool, bool>> _displayPresetVisuals =
        new(StringComparer.OrdinalIgnoreCase);
    private Action<bool>? _displayPresetSaveVisual;
    private string? _selectedDisplayPresetKey;
    private bool _displayPresetUiBuilt;

    private void EnsureDisplayPresetBar()
    {
        if (_displayPresetUiBuilt || _displayRoot == null) return;
        _displayPresetUiBuilt = true;

        var bar = BuildDisplayPresetBar();
        var monitorIndex = _displayMonitorSections == null
            ? -1
            : _displayRoot.Children.IndexOf(_displayMonitorSections);
        if (monitorIndex >= 0)
            _displayRoot.Children.Insert(monitorIndex, bar);
        else
            _displayRoot.Children.Add(bar);

        UpdateDisplayPresetVisuals();
    }

    private FrameworkElement BuildDisplayPresetBar()
    {
        var grid = new Grid
        {
            Height = 42,
            Margin = new Thickness(0, 6, 0, 4)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "Presets",
            Foreground = Brushes.White,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var (key, text) in DisplayPresetSlots)
        {
            var chip = CreateDisplayPresetChip(key, text, out var setVisual);
            chip.Margin = new Thickness(0, 0, 6, 0);
            chips.Children.Add(chip);
            _displayPresetVisuals[key] = setVisual;
        }
        Grid.SetColumn(chips, 1);
        grid.Children.Add(chips);

        var save = CreateDisplayPresetSaveButton(out _displayPresetSaveVisual);
        Grid.SetColumn(save, 2);
        grid.Children.Add(save);

        return grid;
    }

    private Border CreateDisplayPresetChip(
        string key,
        string label,
        out Action<bool, bool> setVisual)
    {
        var text = new TextBlock
        {
            Text = label,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var border = new Border
        {
            Height = 27,
            MinWidth = label.StartsWith("Custom", StringComparison.Ordinal) ? 68 : 52,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 0, 9, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = text
        };

        var selected = false;
        var saved = false;
        void ApplyVisual(bool isSelected, bool isSaved)
        {
            selected = isSelected;
            saved = isSaved;

            if (isSelected)
            {
                border.Background = Frozen("#263FD15B");
                border.BorderBrush = Frozen("#663FD15B");
                text.Foreground = AudioGreen;
            }
            else
            {
                border.Background = AudioComboBg;
                border.BorderBrush = isSaved ? AudioComboBorder : Frozen("#20FFFFFF");
                text.Foreground = isSaved ? Brushes.White : AudioMuted;
                border.Opacity = isSaved ? 1 : 0.7;
            }

            if (isSelected) border.Opacity = 1;
            border.ToolTip = isSaved
                ? $"Apply {label} preset"
                : $"{label} is empty. Select it, then choose Save current.";
        }

        border.MouseEnter += (_, _) =>
        {
            if (!selected) border.Background = AudioComboHover;
        };
        border.MouseLeave += (_, _) => ApplyVisual(selected, saved);
        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            SelectDisplayPreset(key);
        };

        setVisual = ApplyVisual;
        return border;
    }

    private Border CreateDisplayPresetSaveButton(out Action<bool> setVisual)
    {
        var text = new TextBlock
        {
            Text = "Save current",
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var border = new Border
        {
            Height = 28,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = text,
            ToolTip = "Save the current values into the selected preset"
        };

        var enabled = false;
        void ApplyVisual(bool isEnabled)
        {
            enabled = isEnabled;
            border.Cursor = isEnabled ? Cursors.Hand : Cursors.Arrow;
            border.Background = isEnabled ? AudioComboBg : Brushes.Transparent;
            border.BorderBrush = isEnabled ? AudioComboBorder : Frozen("#18FFFFFF");
            text.Foreground = isEnabled ? Brushes.White : AudioMuted;
            border.Opacity = isEnabled ? 1 : 0.45;
        }

        border.MouseEnter += (_, _) =>
        {
            if (enabled) border.Background = AudioComboHover;
        };
        border.MouseLeave += (_, _) => ApplyVisual(enabled);
        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (enabled) SaveSelectedDisplayPreset();
        };

        setVisual = ApplyVisual;
        ApplyVisual(false);
        return border;
    }

    private void SelectDisplayPreset(string key)
    {
        _selectedDisplayPresetKey = key;
        UpdateDisplayPresetVisuals();

        if (!TryGetDisplayPreset(key, out var preset) || preset.Monitors.Count == 0)
            return;

        // Let MonitorWriteScheduler's short debounce window coalesce rapid preset
        // changes. Forcing a flush for every click can issue back-to-back DDC/CI
        // transactions even when the user is simply stepping through presets.
        // View exit still flushes pending values immediately.
        _displayViewModel.ApplyPresetValues(preset.Monitors);
    }

    private void SaveSelectedDisplayPreset()
    {
        if (_selectedDisplayPresetKey == null) return;

        var values = _displayViewModel.CapturePresetValues();
        if (values.Count == 0) return;

        var label = DisplayPresetSlots.First(slot =>
            string.Equals(slot.Key, _selectedDisplayPresetKey, StringComparison.OrdinalIgnoreCase)).Label;

        _settings.DisplayPresets ??= new Dictionary<string, DisplayPresetSettings>(StringComparer.OrdinalIgnoreCase);
        _settings.DisplayPresets[_selectedDisplayPresetKey] = new DisplayPresetSettings
        {
            Name = label,
            Monitors = values
        };
        _settingsService.Save(_settings);
        _displayViewModel.ReportPresetSaved(label);
        UpdateDisplayPresetVisuals();
    }

    private bool TryGetDisplayPreset(string key, out DisplayPresetSettings preset)
    {
        if (_settings.DisplayPresets != null &&
            _settings.DisplayPresets.TryGetValue(key, out var stored) &&
            stored != null)
        {
            preset = stored;
            return true;
        }

        preset = new DisplayPresetSettings();
        return false;
    }

    private void UpdateDisplayPresetVisuals()
    {
        foreach (var (key, _) in DisplayPresetSlots)
        {
            if (!_displayPresetVisuals.TryGetValue(key, out var setVisual)) continue;
            var selected = string.Equals(
                key,
                _selectedDisplayPresetKey,
                StringComparison.OrdinalIgnoreCase);
            var saved = TryGetDisplayPreset(key, out var preset) && preset.Monitors.Count > 0;
            setVisual(selected, saved);
        }

        _displayPresetSaveVisual?.Invoke(
            _selectedDisplayPresetKey != null && _displayViewModel.Monitors.Count > 0);
    }
}
