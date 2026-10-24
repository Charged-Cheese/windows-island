using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DynamicIsland.Core.Services;
using DynamicIsland.Infrastructure;
using DynamicIsland.ViewModels;

namespace DynamicIsland;

/// <summary>设置面板：主题、城市、模块开关、歌词偏移、音量步进、开机自启。</summary>
public partial class SettingsWindow : Window
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly IslandViewModel _viewModel;

    public SettingsWindow(IslandViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        Loaded += (_, _) => RefreshUI();
        RefreshUI();
    }

    private AppSettings Settings => _viewModel.Settings;

    private void RefreshUI()
    {
        ThemeCheckObsidian.Visibility = ThemeManager.CurrentId == "obsidian" ? Visibility.Visible : Visibility.Collapsed;
        ThemeCheckGraphite.Visibility = ThemeManager.CurrentId == "graphite" ? Visibility.Visible : Visibility.Collapsed;
        ThemeCheckPorcelain.Visibility = ThemeManager.CurrentId == "porcelain" ? Visibility.Visible : Visibility.Collapsed;

        LatitudeBox.Text = Settings.Latitude.ToString("0.####", CultureInfo.InvariantCulture);
        LongitudeBox.Text = Settings.Longitude.ToString("0.####", CultureInfo.InvariantCulture);
        WeatherHintText.Text = $"当前：{Settings.CityName} · 经纬度 {Settings.Latitude:0.00}, {Settings.Longitude:0.00}";

        SetToggle(ToggleWeather, Settings.EnableWeather);
        SetToggle(ToggleHardware, Settings.EnableHardware);
        SetToggle(ToggleLyrics, Settings.EnableLyrics);
        SetToggle(ToggleNotifications, Settings.EnableNotifications);
        SetToggle(ToggleMarquee, Settings.MarqueeTitle);
        SetToggle(TogglePrivacy, Settings.EnablePrivacyDots);
        SetToggle(ToggleFullscreen, Settings.EnableFullscreenHide);
        SetToggle(ToggleSystemAlerts, Settings.EnableSystemAlerts);
        SetToggle(ToggleBluetooth, Settings.EnableBluetooth);
        SetToggle(ToggleClipboard, Settings.EnableClipboard);
        SetToggle(TogglePillLyrics, Settings.ShowLyricsInPill);
        SetToggle(TogglePillClock, Settings.ShowPillClock);

        LyricOffsetText.Text = $"{(int)Settings.LyricsOffsetMs} ms";
        VolumeStepText.Text = $"{Math.Round(Settings.VolumeStep * 100):0}%";

        bool autoStart = AutoStartService.IsEnabled;
        SetToggle(ToggleAutoStart, autoStart);
        AutoStartHint.Text = autoStart ? "随 Windows 登录启动" : "仅在手动运行时出现";
    }

    private static void SetToggle(TextBlock target, bool on)
        => target.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- 主题 ----------------

    private void Theme_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: string id })
        {
            return;
        }

        ThemeManager.Apply(id);
        Settings.Theme = ThemeManager.CurrentId;
        _viewModel.SaveSettings();
        RefreshUI();
    }

    // ---------------- 城市 ----------------

    private void City_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        string[] parts = tag.Split('|');
        if (parts.Length != 3 ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
        {
            return;
        }

        Settings.CityName = parts[0];
        Settings.Latitude = lat;
        Settings.Longitude = lon;
        _viewModel.ApplySettings();
        RefreshUI();
        WeatherHintText.Text = $"已切换到 {parts[0]}，天气正在刷新…";
    }

    private async void SearchCity_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        string name = CitySearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            WeatherHintText.Text = "请输入城市名，例如：青岛";
            return;
        }

        WeatherHintText.Text = "正在查询…";
        try
        {
            string url =
                "https://geocoding-api.open-meteo.com/v1/search?count=1&language=zh&format=json&name=" +
                Uri.EscapeDataString(name);
            string json = await Http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            {
                WeatherHintText.Text = $"没有找到“{name}”，可以手动填经纬度";
                return;
            }

            var first = results[0];
            string display = first.TryGetProperty("name", out var n) ? n.GetString() ?? name : name;
            if (first.TryGetProperty("admin1", out var admin) && !string.IsNullOrWhiteSpace(admin.GetString()))
            {
                display += " · " + admin.GetString();
            }

            Settings.CityName = display.Length > 18 ? display[..18] : display;
            Settings.Latitude = first.GetProperty("latitude").GetDouble();
            Settings.Longitude = first.GetProperty("longitude").GetDouble();
            _viewModel.ApplySettings();
            RefreshUI();
            WeatherHintText.Text = $"已定位：{Settings.CityName}（{Settings.Latitude:0.00}, {Settings.Longitude:0.00}）";
        }
        catch (Exception ex)
        {
            WeatherHintText.Text = "查询失败：" + ex.Message;
        }
    }

    // ---------------- 模块开关 ----------------

    private void LatLon_LostFocus(object sender, RoutedEventArgs e)
    {
        bool changed = false;
        if (double.TryParse(LatitudeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) &&
            lat is >= -90 and <= 90 && Math.Abs(lat - Settings.Latitude) > 0.0001)
        {
            Settings.Latitude = lat;
            changed = true;
        }

        if (double.TryParse(LongitudeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) &&
            lon is >= -180 and <= 180 && Math.Abs(lon - Settings.Longitude) > 0.0001)
        {
            Settings.Longitude = lon;
            changed = true;
        }

        if (changed)
        {
            _viewModel.ApplySettings();
        }

        RefreshUI();
    }

    private void Toggle_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { Tag: string key })
        {
            return;
        }

        switch (key)
        {
            case "weather":
                Settings.EnableWeather = !Settings.EnableWeather;
                break;
            case "hardware":
                Settings.EnableHardware = !Settings.EnableHardware;
                break;
            case "lyrics":
                Settings.EnableLyrics = !Settings.EnableLyrics;
                break;
            case "notifications":
                Settings.EnableNotifications = !Settings.EnableNotifications;
                break;
            case "privacy":
                Settings.EnablePrivacyDots = !Settings.EnablePrivacyDots;
                break;
            case "fullscreen":
                Settings.EnableFullscreenHide = !Settings.EnableFullscreenHide;
                break;
            case "system-alerts":
                Settings.EnableSystemAlerts = !Settings.EnableSystemAlerts;
                break;
            case "bluetooth":
                Settings.EnableBluetooth = !Settings.EnableBluetooth;
                break;
            case "clipboard":
                Settings.EnableClipboard = !Settings.EnableClipboard;
                break;
            case "pill-lyrics":
                Settings.ShowLyricsInPill = !Settings.ShowLyricsInPill;
                break;
            case "pill-clock":
                Settings.ShowPillClock = !Settings.ShowPillClock;
                break;
        }

        _viewModel.ApplySettings();
        RefreshUI();
    }

    private void Marquee_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Settings.MarqueeTitle = !Settings.MarqueeTitle;
        _viewModel.ApplySettings();
        RefreshUI();
    }

    // ---------------- 细调 ----------------

    private void LyricOffsetMinus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Settings.LyricsOffsetMs = Math.Clamp(Settings.LyricsOffsetMs - 50, -1500, 1500);
        _viewModel.ApplySettings();
        RefreshUI();
    }

    private void LyricOffsetPlus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Settings.LyricsOffsetMs = Math.Clamp(Settings.LyricsOffsetMs + 50, -1500, 1500);
        _viewModel.ApplySettings();
        RefreshUI();
    }

    private void VolumeStepMinus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Settings.VolumeStep = Math.Clamp(Settings.VolumeStep - 0.01, 0.01, 0.2);
        _viewModel.ApplySettings();
        RefreshUI();
    }

    private void VolumeStepPlus_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Settings.VolumeStep = Math.Clamp(Settings.VolumeStep + 0.01, 0.01, 0.2);
        _viewModel.ApplySettings();
        RefreshUI();
    }

    private void AutoStart_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (AutoStartService.IsEnabled)
        {
            AutoStartService.Disable();
        }
        else
        {
            AutoStartService.Enable();
        }

        RefreshUI();
    }

    // ---------------- 底部 ----------------

    private void OpenDataDir_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            AppPaths.EnsureCreated();
            Process.Start("explorer.exe", AppPaths.Root);
        }
        catch (Exception ex)
        {
            LogService.Warn("打开数据目录失败：" + ex.Message);
        }
    }

    private void OpenLog_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        LauncherService.RevealInExplorer(AppPaths.LogFile);
    }

    private void Close_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Close();
    }

    /// <summary>标题栏的 DragMove 会吞掉关闭按钮的 MouseUp，这里在 preview 阶段拦下。</summary>
    private void CloseButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 快速双击等情况忽略
            }
        }
    }
}
