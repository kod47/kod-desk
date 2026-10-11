using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KodDesk.App;
using KodDesk.Core;
using KodDesk.Core.Viewer;

/// <summary>
/// Deschide fereastra de control și exploratorul la distanță peste o sesiune reală, ca să prindă erorile XAML
/// (resurse lipsă, legături greșite) și să verifice că exploratorul încarcă lista de fișiere.
/// </summary>
internal static class UiSmokeTest
{
    public static (bool ok, string detail) Run(ViewerSession session, ViewerSettings viewer)
    {
        bool ok = false;
        string detail = "";
        var t = new Thread(() =>
        {
            try
            {
                var app = new App { Mode = AppMode.ResourcesOnly };
                app.InitializeComponent();
                typeof(App).GetProperty(nameof(App.Viewer))!.SetValue(null, viewer);

                var vw = new ViewerWindow(session);
                vw.Show();
                var rf = new RemoteFilesWindow(session, new TransferManager(session));
                rf.Show();

                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                Dispatcher.PushFrame(frame);

                var list = (ListView)typeof(RemoteFilesWindow).GetField("List", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rf)!;
                var status = (TextBlock)typeof(RemoteFilesWindow).GetField("StatusText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rf)!;
                ok = list.Items.Count > 0;
                detail = $"{list.Items.Count} elemente în explorator, stare „{status.Text}”";
                rf.Close();
                vw.Hide(); // închiderea ar elibera sesiunea, pe care o eliberează testul
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message + (ex.InnerException != null ? " / " + ex.InnerException.Message : "");
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return (ok, detail);
    }
}
