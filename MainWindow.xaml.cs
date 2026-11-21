using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RgbStudio.Views;
using Wpf.Ui.Controls;

namespace RgbStudio;

public partial class MainWindow : FluentWindow
{
    readonly Dictionary<string, UserControl> _pages = new();
    bool _syncing;

    public MainWindow()
    {
        InitializeComponent();
        App.Current.StatusChanged += () => Dispatcher.BeginInvoke(RefreshHeader);
        App.Engine.SceneEdited += () => Dispatcher.BeginInvoke(RefreshHeader);
        App.Current.UpdateChanged += () => Dispatcher.BeginInvoke(RefreshUpdateBar);
        Nav.SelectedIndex = 0;
        RefreshHeader();
        RefreshUpdateBar();
    }

    // ---- Guncelleme seridi ----

    string? _dismissedTag;

    void RefreshUpdateBar()
    {
        var u = App.Current.AvailableUpdate;
        bool show = u != null && (u.Tag != _dismissedTag || App.Current.IsUpdating);
        UpdateBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (u == null) return;
        UpdateTitle.Text = $"Yeni sürüm var: {u.Tag}";
        if (!App.Current.IsUpdating)
            UpdateText.Text = $"Kurulu sürüm {Core.Updater.Current}. Güncelle'ye basınca GitHub'dan indirilir ({u.Size / 1048576.0:0.0} MB), doğrulanır ve RGB Studio yeniden açılır.";
        UpdateButton.IsEnabled = UpdateLaterButton.IsEnabled = !App.Current.IsUpdating;
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        var progress = new Progress<double>(p => UpdateText.Text = $"İndiriliyor… %{p * 100:0}");
        UpdateText.Text = "İndiriliyor…";
        var err = await App.Current.InstallUpdateAsync(progress);
        if (err != null) UpdateText.Text = err;
    }

    void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.AvailableUpdate is { } u)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.PageUrl) { UseShellExecute = true });
    }

    void UpdateLater_Click(object sender, RoutedEventArgs e)
    {
        _dismissedTag = App.Current.AvailableUpdate?.Tag;
        RefreshUpdateBar();
    }

    UserControl Page(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = key switch
            {
                "scenes" => new ScenesView(),
                "settings" => new SettingsView(),
                "about" => new AboutView(),
                _ => new DevicesView(),
            };
            _pages[key] = page;
        }
        return page;
    }

    void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Host == null) return;
        if (Nav.SelectedItem is ListBoxItem { Tag: string key }) Host.Content = Page(key);
    }

    public void Navigate(string key)
    {
        foreach (ListBoxItem item in Nav.Items)
            if ((string)item.Tag == key) { Nav.SelectedItem = item; return; }
    }

    void RefreshHeader()
    {
        _syncing = true;
        bool off = App.Settings.MasterOff;
        MasterSwitch.IsChecked = !off;
        MasterText.Text = off ? "Kapalı — tüm cihazlar sönük" : "Açık — senaryo uygulanıyor";
        MasterIcon.Symbol = off ? SymbolRegular.LightbulbCircle24 : SymbolRegular.Lightbulb24;
        _syncing = false;

        var name = App.Engine.Read(s => s.Name);
        int n = App.Engine.Devices.Count;
        SceneText.Text = $"Etkin senaryo: {name}   ·   {n} cihaz";
        StatusLine.Text = App.Current.StatusText;
        ScanButton.IsEnabled = !App.Current.IsScanning;
    }

    void MasterSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        App.Current.SetMasterOff(MasterSwitch.IsChecked != true);
    }

    async void Scan_Click(object sender, RoutedEventArgs e) => await App.Current.ScanAsync();

    protected override void OnClosing(CancelEventArgs e)
    {
        // Kapatma dugmesi uygulamayi bitirmez: isiklar ve uyku sonrasi geri yukleme icin arka planda kalir.
        if (App.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnClosing(e);
        App.Current.ExitApp();
    }
}
