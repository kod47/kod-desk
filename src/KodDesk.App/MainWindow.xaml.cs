using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using KodDesk.Core;
using KodDesk.Core.Host;
using KodDesk.Core.Service;
using KodDesk.Core.Viewer;
using KodDesk.Protocol;

namespace KodDesk.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RefreshRecent();
        RefreshHost();
        RefreshSavedPassword();

        App.Host.Changed += () => Dispatcher.BeginInvoke(RefreshHost);

        Loaded += (_, _) =>
        {
            if (!App.Viewer.IsRelayConfigured) OpenSettings();
        };
    }

    // ---------------- Acest calculator ----------------

    private void RefreshHost()
    {
        var h = App.Host;
        MyIdText.Text = h.HostId != null ? HostSettings.FormatId(h.HostId) : "--- --- ---";

        var (brush, text) = h.Status switch
        {
            HostStatus.Online => ((Brush)FindResource("SuccessBrush"), "Online — gata de conexiuni"),
            HostStatus.NotConfigured => ((Brush)FindResource("WarningBrush"),
                App.ServiceMode ? "Serviciul nu are relay configurat (Setări serviciu)" : "Serverul relay nu este configurat (vezi Setări)"),
            HostStatus.Error => ((Brush)FindResource("DangerBrush"), "Eroare: " + h.Message),
            _ => ((Brush)FindResource("MutedBrush"), "Se conectează la relay..."),
        };
        StatusDot.Fill = brush;
        StatusText.Text = text;
        StatusText.ToolTip = text;

        PasswordStateText.Text = h.HasPassword
            ? "Parola este setată. Vă puteți conecta la acest calculator cu ID-ul și parola."
            : "Nu este setată nicio parolă — nimeni nu se poate conecta la acest calculator.";

        if (App.ServiceMode)
        {
            PasswordButton.Content = RelayConfig.IsEmbedded ? "Setări serviciu (parolă)..." : "Setări serviciu (parolă, relay)...";
            ServiceStateText.Text = "Instalat ✓ — pornește automat cu Windows; acces și la ecranul de login, UAC și Ctrl+Alt+Del.";
        }
        else
        {
            PasswordButton.Content = "Setează parola...";
            ServiceStateText.Text = "Neinstalat — acest calculator este accesibil doar cât aplicația este deschisă. " +
                                    "Instalați KodDesk cu installer-ul (KodDesk-x.y.z-x64.msi) pentru acces după repornire.";
        }

        bool controlled = h.ActiveSessions > 0;
        if (controlled && ControlledBanner.Visibility != Visibility.Visible && WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        ControlledBanner.Visibility = controlled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MyId_Click(object sender, MouseButtonEventArgs e)
    {
        if (App.Host.HostId is { } id)
            try { Clipboard.SetText(id); } catch { }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings(bool focusPassword = false)
    {
        var mode = App.ServiceMode ? SettingsMode.Viewer : SettingsMode.Portable;
        var dlg = new SettingsWindow(mode, focusPassword) { Owner = this };
        if (dlg.ShowDialog() == true) RefreshHost();
    }

    private async void PasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!App.ServiceMode)
        {
            OpenSettings(focusPassword: true);
            return;
        }
        IsEnabled = false;
        try
        {
            await Task.Run(() => ServiceManager.RunElevated("--service-settings", out _));
            (App.Host as ServiceHostStatus)?.Refresh();
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void DisconnectIncoming_Click(object sender, RoutedEventArgs e) => App.Host.Disconnect();

    // ---------------- Controlează alt calculator ----------------

    private void RefreshRecent()
    {
        var current = RemoteIdBox.Text;
        RemoteIdBox.Items.Clear();
        foreach (var id in App.Viewer.RecentIds) RemoteIdBox.Items.Add(HostSettings.FormatId(id));
        RemoteIdBox.Text = string.IsNullOrEmpty(current) && RemoteIdBox.Items.Count > 0 ? (string)RemoteIdBox.Items[0] : current;
    }

    private void RemoteIdBox_Changed(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(RefreshSavedPassword);

    private void RefreshSavedPassword()
    {
        bool saved = App.Viewer.HasSavedKey(RemoteIdBox.Text);
        SavedPasswordHint.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        RememberCheck.IsChecked = saved;
    }

    private void ConnectField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Connect_Click(sender, e);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectErrorText.Text = "";
        var id = SecureHandshake.NormalizeId(RemoteIdBox.Text);
        if (id.Length < 6)
        {
            ConnectErrorText.Text = "Introduceți ID-ul calculatorului.";
            return;
        }
        if (!App.Viewer.IsRelayConfigured)
        {
            ConnectErrorText.Text = "Configurați mai întâi serverul relay (Setări).";
            return;
        }

        var password = RemotePasswordBox.Password.Length > 0 ? RemotePasswordBox.Password : null;
        var savedKey = password == null ? App.Viewer.GetSavedKey(id) : null;
        if (password == null && savedKey == null)
        {
            ConnectErrorText.Text = "Introduceți parola.";
            return;
        }

        bool remember = RememberCheck.IsChecked == true;
        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "Se conectează...";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var session = await Task.Run(() => ViewerSession.ConnectAsync(App.Viewer, id, password, savedKey, cts.Token));
            App.Viewer.AddRecent(id);
            App.Viewer.SetSavedKey(id, remember ? session.PasswordKey : null);
            App.Viewer.Save();
            RemotePasswordBox.Clear();
            RefreshRecent();
            RemoteIdBox.Text = HostSettings.FormatId(id);
            RefreshSavedPassword();
            new ViewerWindow(session).Show();
        }
        catch (AuthenticationFailedException ex)
        {
            if (savedKey != null)
            {
                // Parola s-a schimbat pe calculatorul la distanță: uităm cheia memorată.
                App.Viewer.SetSavedKey(id, null);
                App.Viewer.Save();
                RefreshSavedPassword();
                ConnectErrorText.Text = "Parola memorată nu mai este valabilă. Introduceți parola din nou.";
            }
            else
            {
                ConnectErrorText.Text = ex.Message;
            }
        }
        catch (OperationCanceledException)
        {
            ConnectErrorText.Text = "Conexiunea a expirat.";
        }
        catch (Exception ex)
        {
            Log.Warn("Conectare eșuată", ex);
            ConnectErrorText.Text = ex.Message;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
            ConnectButton.Content = "Conectare";
        }
    }
}
