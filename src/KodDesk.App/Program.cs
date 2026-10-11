using System.ServiceProcess;
using KodDesk.Core.Service;

namespace KodDesk.App;

/// <summary>
/// Un singur executabil, mai multe roluri:
///   KodDesk.exe                     aplicația (fereastra principală)
///   KodDesk.exe --service           serviciul Windows (pornit de Service Control Manager)
///   KodDesk.exe --agent TOKEN       agentul unei sesiuni (pornit de serviciu în sesiunea utilizatorului)
///   KodDesk.exe --service-settings  setările serviciului (cu drepturi de administrator)
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "";
        switch (mode)
        {
            case "--service":
                ServiceBase.Run(new KodDeskService());
                return 0;

            case "--agent" when args.Length > 1:
                return AgentRunner.Run(args[1]);
        }

        var app = new App { Mode = mode == "--service-settings" ? AppMode.ServiceSettings : AppMode.Normal };
        app.InitializeComponent();
        return app.Run();
    }
}
