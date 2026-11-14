using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using LanPod.Services;
using LanPod.ViewModels;

namespace LanPod;

public partial class MainWindow
{
    private string _connectionState = "尚未连接";
    public bool IsRouterConnected => _connectionState == "已连接";

    private async void InterfaceLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingAppSettings || !IsInitialized) return;
        var language = (InterfaceLanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        ApplyInterfaceLanguage(LocalizationService.NormalizeLanguage(language));
        try
        {
            await _repository.UpdateInterfaceLanguageAsync(LocalizationService.Language);
            _lanWebServer?.SetPreferences(_repository.Settings, _hasSavedRouterPassword);
        }
        catch (Exception exception)
        {
            ShowSettings(LocalizationService.Format("保存界面语言失败：{0}", exception.Message));
        }
    }

    private void ApplyInterfaceLanguage(string language)
    {
        LocalizationService.Apply(language);
        Language = XmlLanguage.GetLanguage(LocalizationService.Language);
        foreach (var device in _deviceViewModelsByMac.Values) device.RefreshLanguage();
        foreach (var session in _detailSessionItems) session.RefreshLanguage();
        var selectedType = (DeviceTypeCombo.SelectedItem as DeviceTypeOption)?.TypeName;
        _isPopulatingDeviceSettings = true;
        try
        {
            DeviceTypeCombo.ItemsSource = DeviceIdentityService.AvailableTypes
                .Select(identity => new DeviceTypeOption(identity.TypeName, identity.IconGlyph)).ToList();
            DeviceTypeCombo.SelectedItem = DeviceTypeCombo.Items.Cast<DeviceTypeOption>()
                .FirstOrDefault(option => option.TypeName == selectedType);
        }
        finally { _isPopulatingDeviceSettings = false; }
        MeshNodesItemsControl.Items.Refresh();
        UpdateSelectedDevice(SelectedDevice, updateEditor: false);
        OnPropertyChanged(nameof(HistoryActionName));
        UpdateWindowStateButtons();
    }

    private void SetConnectionStatus(string state)
    {
        _connectionState = state;
        LocalizationService.SetText(ConnectionStatusText, state);
        OnPropertyChanged(nameof(IsRouterConnected));
    }
}
