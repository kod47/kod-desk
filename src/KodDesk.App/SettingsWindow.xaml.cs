using System.Windows;
using System.Windows.Interop;
using KodDesk.Core;
using KodDesk.Core.Service;
using KodDesk.Protocol;

namespace KodDesk.App;

public enum SettingsMode
{
    /// <summary>Fără serviciu: relay, parola acestui calculator, calitate.</summary>
    Portable,
    /// <summary>Cu serviciu: doar setările pentru conectarea la alte calculatoare.</summary>
    Viewer,
    /// <summary>Setările serviciului (proces cu drepturi de administrator): relay și parolă.</summary>
    Service,
}

public partial class SettingsWindow : Window
{
    private static readonly (int kbps, string label)[] s_bitrates =
    [
        (4000, "4 Mbps (conexiune lentă)"),
        (8000, "8 Mbps"),
        (12000, "12 Mbps (recomandat)"),
        (20000, "20 Mbps"),
        (40000, "40 Mbps (calitate maximă)"),
    ];

    private static readonly int[] s_fps = [30, 60];

    private readonly SettingsMode _mode;
    private readonly HostSettings? _host;

    public SettingsWindow(SettingsMode mode, bool focusPassword = false)
    {
        InitializeComponent();
        _mode = mode;

        switch (mode)
        {
            case SettingsMode.Portable:
                _host = (App.Host as LocalHostStatus)?.Settings;
                RelayAddressBox.Text = App.Viewer.RelayAddress;
                RelayKeyBox.Password = App.Viewer.RelayKey;
                break;

            case SettingsMode.Viewer:
                PasswordCard.Visibility = Visibility.Collapsed;
                ViewerModeHint.Visibility = Visibility.Visible;
                RelayAddressBox.Text = App.Viewer.RelayAddress;
                RelayKeyBox.Password = App.Viewer.RelayKey;
                break;

            case SettingsMode.Service:
                Title = "Setări serviciu — KodDesk";
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ServiceHeader.Visibility = Visibility.Visible;
                QualityCard.Visibility = Visibility.Collapsed;
                SaveButton.Content = "Salvează și repornește serviciul";
                try
                {
                    _host = HostSettings.Load();
                }
                catch (Exception ex)
                {
                    ErrorText.Text = "Nu pot citi setările serviciului: " + ex.Message;
                    SaveButton.IsEnabled = false;
                }
                RelayAddressBox.Text = _host?.RelayAddress ?? "";
                RelayKeyBox.Password = _host?.RelayKey ?? "";
                break;
        }

        // Relay-ul încorporat în aplicație nu se afișează și nu se poate modifica.
        if (RelayConfig.IsEmbedded) RelayCard.Visibility = Visibility.Collapsed;

        PasswordHint.Text = _host?.HasPassword == true
            ? "O parolă este deja setată. Lăsați câmpurile goale pentru a o păstra."
            : "Nu este setată nicio parolă. Fără parolă, nimeni nu se poate conecta la acest calculator.";

        if (mode != SettingsMode.Service)
        {
            foreach (var (_, label) in s_bitrates) BitrateBox.Items.Add(label);
            var bi = Array.FindIndex(s_bitrates, b => b.kbps == App.Viewer.BitrateKbps);
            BitrateBox.SelectedIndex = bi >= 0 ? bi : 2;

            foreach (var f in s_fps) FpsBox.Items.Add($"{f} fps");
            var fi = Array.IndexOf(s_fps, App.Viewer.MaxFps);
            FpsBox.SelectedIndex = fi >= 0 ? fi : 1;
        }

        Loaded += (_, _) =>
        {
            if (focusPassword && PasswordCard.IsVisible) NewPasswordBox.Focus();
            else if (RelayCard.IsVisible) RelayAddressBox.Focus();
        };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";

        bool editRelay = !RelayConfig.IsEmbedded;
        var address = RelayAddressBox.Text.Trim();
        if (editRelay && address.Length > 0)
        {
            try { RelayUrls.BaseUri(address); }
            catch
            {
                ErrorText.Text = "Adresa relay-ului nu este validă.";
                return;
            }
        }

        string? newPassword = null;
        bool removePassword = PasswordCard.IsVisible && RemovePasswordCheck.IsChecked == true;
        if (PasswordCard.IsVisible && !removePassword && (NewPasswordBox.Password.Length > 0 || ConfirmPasswordBox.Password.Length > 0))
        {
            if (NewPasswordBox.Password.Length < 8)
            {
                ErrorText.Text = "Parola trebuie să aibă cel puțin 8 caractere.";
                return;
            }
            if (NewPasswordBox.Password != ConfirmPasswordBox.Password)
            {
                ErrorText.Text = "Parolele nu coincid.";
                return;
            }
            newPassword = NewPasswordBox.Password;
        }

        try
        {
            if (_mode != SettingsMode.Service)
            {
                var v = App.Viewer;
                if (editRelay)
                {
                    v.RelayAddress = address;
                    v.RelayKey = RelayKeyBox.Password;
                }
                v.BitrateKbps = s_bitrates[Math.Max(BitrateBox.SelectedIndex, 0)].kbps;
                v.MaxFps = s_fps[Math.Max(FpsBox.SelectedIndex, 0)];
                v.Save();
            }

            if (_mode is SettingsMode.Portable or SettingsMode.Service && _host != null)
            {
                bool relayChanged = editRelay && (address != _host!.RelayAddress || RelayKeyBox.Password != _host.RelayKey);
                bool passwordChanged = removePassword || newPassword != null;
                if (editRelay)
                {
                    _host!.RelayAddress = address;
                    _host.RelayKey = RelayKeyBox.Password;
                }
                if (removePassword) _host.SetPassword(null);
                else if (newPassword != null) _host.SetPassword(newPassword);
                _host.Save();

                if (_mode == SettingsMode.Portable && (relayChanged || passwordChanged))
                    (App.Host as LocalHostStatus)?.Apply(_host);
                if (_mode == SettingsMode.Service)
                {
                    IsEnabled = false;
                    ServiceManager.Restart();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Nu pot salva setările", ex);
            ErrorText.Text = "Nu pot salva setările: " + ex.Message;
            IsEnabled = true;
            return;
        }

        CloseWithResult(true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWithResult(false);

    private void CloseWithResult(bool result)
    {
        if (ComponentDispatcher.IsThreadModal && Owner != null)
            DialogResult = result;
        else
            Close();
    }
}
