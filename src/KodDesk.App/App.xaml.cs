using System.Windows;
using System.Windows.Threading;
using KodDesk.Core;
using KodDesk.Core.Service;

namespace KodDesk.App;

public enum AppMode
{
    Normal,
    /// <summary>Setările serviciului, pornit cu drepturi de administrator.</summary>
    ServiceSettings,
    /// <summary>Doar resursele (testele de interfață): nu pornește nimic automat.</summary>
    ResourcesOnly,
}

public partial class App : Application
{
    private static Mutex? s_singleInstance;

    public AppMode Mode { get; init; }

    public static ViewerSettings Viewer { get; private set; } = null!;
    public static IHostStatusSource Host { get; private set; } = null!;

    /// <summary>True dacă serviciul Windows este instalat (atunci el ține calculatorul online, nu aplicația).</summary>
    public static bool ServiceMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Excepție netratată", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("Task neobservat", args.Exception);
            args.SetObserved();
        };

        if (Mode == AppMode.ResourcesOnly) return;

        if (Mode == AppMode.ServiceSettings)
        {
            Log.Init("settings", machineWide: true);
            var w = new SettingsWindow(SettingsMode.Service);
            MainWindow = w;
            w.Show();
            return;
        }

        Log.Init("app", machineWide: false);
        Log.Info($"Pornire KodDesk {typeof(App).Assembly.GetName().Version} din {AppContext.BaseDirectory}");

        s_singleInstance = new Mutex(true, @"Local\KodDesk.App", out bool first);
        if (!first)
        {
            MessageBox.Show("KodDesk este deja pornit.", "KodDesk", MessageBoxButton.OK, MessageBoxImage.Information);
            s_singleInstance.Dispose();
            s_singleInstance = null;
            Shutdown();
            return;
        }

        Viewer = ViewerSettings.Load();
        ServiceMode = ServiceManager.IsInstalled();
        Host = ServiceMode ? new ServiceHostStatus() : new LocalHostStatus();
        Host.Start();

        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Host?.Dispose();
        Log.Info("Ieșire");
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Excepție netratată în UI", e.Exception);
        MessageBox.Show(e.Exception.Message, "KodDesk — eroare", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
