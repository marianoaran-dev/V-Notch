using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VNotch.Models;

namespace VNotch;

public partial class PiggyAlertSettingsWindow : Window
{
    private readonly NotchSettings _source;
    private readonly Action<bool> _sendTestNotification;

    public PiggyAlertSettingsWindow(NotchSettings settings, Action<bool> sendTestNotification)
    {
        InitializeComponent();
        _source = settings;
        _sendTestNotification = sendTestNotification;
        LoadSettings();
    }

    private void LoadSettings()
    {
        EnableNotificationsCheck.IsChecked = _source.EnablePiggyNotifications;
        UsageAlertsCheck.IsChecked = _source.PiggyUsageAlertsEnabled;
        Alert50Check.IsChecked = _source.PiggyAlertAt50;
        Alert25Check.IsChecked = _source.PiggyAlertAt25;
        Alert10Check.IsChecked = _source.PiggyAlertAt10;
        CustomAlertCheck.IsChecked = _source.PiggyCustomAlertEnabled;
        CustomAlertText.Text = Math.Clamp(_source.PiggyCustomAlertPercent, 1, 99).ToString();
        BankedExpiryCheck.IsChecked = _source.PiggyBankedResetExpiryAlerts;
        NotificationSoundCheck.IsChecked = _source.PiggyNotificationSound;

        BankedReminderCombo.SelectedItem = BankedReminderCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item =>
                int.TryParse(item.Tag?.ToString(), out int hours)
                && hours == _source.PiggyBankedResetReminderHours)
            ?? BankedReminderCombo.Items.OfType<ComboBoxItem>().Last();

        UpdateEnabledState();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MasterNotificationCheck_Changed(object sender, RoutedEventArgs e)
        => UpdateEnabledState();

    private void UsageAlertsCheck_Changed(object sender, RoutedEventArgs e)
        => UpdateEnabledState();

    private void CustomAlertCheck_Changed(object sender, RoutedEventArgs e)
        => UpdateEnabledState();

    private void BankedExpiryCheck_Changed(object sender, RoutedEventArgs e)
        => UpdateEnabledState();

    private void UpdateEnabledState()
    {
        bool master = EnableNotificationsCheck.IsChecked == true;
        UsageAlertsPanel.IsEnabled = master;
        BankedAlertsPanel.IsEnabled = master;
        NotificationSoundCheck.IsEnabled = master;
        TestButton.IsEnabled = master;

        bool usage = master && UsageAlertsCheck.IsChecked == true;
        Alert50Check.IsEnabled = usage;
        Alert25Check.IsEnabled = usage;
        Alert10Check.IsEnabled = usage;
        CustomAlertCheck.IsEnabled = usage;
        CustomAlertText.IsEnabled = usage && CustomAlertCheck.IsChecked == true;

        BankedReminderCombo.IsEnabled = master && BankedExpiryCheck.IsChecked == true;
    }

    private void CustomAlertText_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
    }

    private void Test_Click(object sender, RoutedEventArgs e)
        => _sendTestNotification(NotificationSoundCheck.IsChecked == true);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(CustomAlertText.Text, out int customPercent))
            customPercent = 60;

        _source.EnablePiggyNotifications = EnableNotificationsCheck.IsChecked == true;
        _source.PiggyUsageAlertsEnabled = UsageAlertsCheck.IsChecked == true;
        _source.PiggyAlertAt50 = Alert50Check.IsChecked == true;
        _source.PiggyAlertAt25 = Alert25Check.IsChecked == true;
        _source.PiggyAlertAt10 = Alert10Check.IsChecked == true;
        _source.PiggyCustomAlertEnabled = CustomAlertCheck.IsChecked == true;
        _source.PiggyCustomAlertPercent = Math.Clamp(customPercent, 1, 99);
        _source.PiggyBankedResetExpiryAlerts = BankedExpiryCheck.IsChecked == true;
        _source.PiggyBankedResetReminderHours = BankedReminderCombo.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out int hours)
                ? hours
                : 48;
        _source.PiggyNotificationSound = NotificationSoundCheck.IsChecked == true;

        DialogResult = true;
        Close();
    }
}
