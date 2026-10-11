using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KodDesk.Core;
using KodDesk.Core.Viewer;
using KodDesk.Protocol;

namespace KodDesk.App;

public partial class ViewerWindow : Window
{
    private static readonly (int kbps, string label)[] s_bitrates =
    [
        (4000, "4 Mbps"),
        (8000, "8 Mbps"),
        (12000, "12 Mbps"),
        (20000, "20 Mbps"),
        (40000, "40 Mbps"),
    ];

    private readonly ViewerSession _session;
    private readonly KeyboardHook _hook;
    private readonly DispatcherTimer _statsTimer;
    private readonly TransferManager _transfers;
    private RemoteFilesWindow? _explorer;
    private WriteableBitmap? _bitmap;
    private bool _oneToOne;
    private bool _fullscreen;
    private WindowState _prevState;
    private bool _suppressSelection;
    private bool _closing;
    private ushort _lastX = ushort.MaxValue, _lastY = ushort.MaxValue;

    public ViewerWindow(ViewerSession session)
    {
        InitializeComponent();
        _session = session;

        var name = $"{session.HostName} ({HostSettings.FormatId(session.HostId)})";
        Title = name + " — KodDesk";
        TitleText.Text = name;

        _suppressSelection = true;
        foreach (var (_, label) in s_bitrates) QualityBox.Items.Add(label);
        var bi = Array.FindIndex(s_bitrates, b => b.kbps == App.Viewer.BitrateKbps);
        QualityBox.SelectedIndex = bi >= 0 ? bi : 2;
        _suppressSelection = false;

        _hook = new KeyboardHook((scan, ext, down) => _session.SendKey(scan, ext, down));

        _transfers = new TransferManager(session);
        TransfersList.ItemsSource = _transfers.Items;
        ClipboardMenu.IsEnabled = session.Clipboard != null;
        ClipboardMenu.IsChecked = session.Clipboard?.Enabled ?? false;

        _session.SessionInfoReceived += info => Dispatcher.BeginInvoke(() => PopulateMonitors(info));
        _session.FormatChanged += f => Dispatcher.BeginInvoke(() => SelectMonitorInBox(f.MonitorIndex));
        _session.Closed += reason => Dispatcher.BeginInvoke(() => OnSessionClosed(reason));
        if (_session.Info != null) PopulateMonitors(_session.Info);

        Activated += (_, _) => _hook.Install();
        Deactivated += (_, _) =>
        {
            _hook.Uninstall();
            _session.SendReleaseAllKeys();
        };
        DpiChanged += (_, _) => ApplyScaleMode();

        CompositionTarget.Rendering += OnRendering;

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += (_, _) => UpdateStats();
        _statsTimer.Start();

        Closed += OnWindowClosed;
    }

    // ---------------- Imagine ----------------

    private void OnRendering(object? sender, EventArgs e)
    {
        _session.Frames.TryReadLatest((ptr, w, h, stride) =>
        {
            if (_bitmap == null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                Screen.Source = _bitmap;
                Overlay.Visibility = Visibility.Collapsed;
                ApplyScaleMode();
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), ptr, stride * h, stride);
        });
    }

    private void ApplyScaleMode()
    {
        if (_oneToOne && _bitmap != null)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            Screen.Stretch = Stretch.Fill;
            Screen.Width = _bitmap.PixelWidth / dpi.DpiScaleX;
            Screen.Height = _bitmap.PixelHeight / dpi.DpiScaleY;
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            RenderOptions.SetBitmapScalingMode(Screen, BitmapScalingMode.NearestNeighbor);
            ScaleButton.Content = "Potrivire în fereastră";
        }
        else
        {
            Screen.Stretch = Stretch.Uniform;
            Screen.Width = double.NaN;
            Screen.Height = double.NaN;
            Scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            RenderOptions.SetBitmapScalingMode(Screen, BitmapScalingMode.HighQuality);
            ScaleButton.Content = "Mărime reală (1:1)";
        }
    }

    private void UpdateStats()
    {
        var s = _session.GetStats();
        if (s.Width == 0) return;
        StatsText.Text = $"{s.Width}×{s.Height} · {s.Encoder} {(s.Yuv444 ? "4:4:4" : "4:2:0")} · {s.Fps:F0} fps · {s.Mbps:F1} Mbps · {s.RttMs} ms";
    }

    // ---------------- Bara de unelte ----------------

    private void PopulateMonitors(SessionInfo info)
    {
        _suppressSelection = true;
        MonitorBox.Items.Clear();
        for (int i = 0; i < info.Monitors.Count; i++)
        {
            var m = info.Monitors[i];
            MonitorBox.Items.Add($"Monitor {i + 1}: {m.Width}×{m.Height}{(m.Primary ? " (principal)" : "")}");
        }
        MonitorBox.SelectedIndex = info.CurrentMonitor < info.Monitors.Count ? info.CurrentMonitor : 0;
        MonitorBox.Visibility = info.Monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _suppressSelection = false;
    }

    private void SelectMonitorInBox(int index)
    {
        if (index < 0 || index >= MonitorBox.Items.Count) return;
        _suppressSelection = true;
        MonitorBox.SelectedIndex = index;
        _suppressSelection = false;
    }

    private void MonitorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || MonitorBox.SelectedIndex < 0) return;
        _session.SelectMonitor(MonitorBox.SelectedIndex);
    }

    private void QualityBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || QualityBox.SelectedIndex < 0) return;
        _session.SetQuality(new QualitySettings(s_bitrates[QualityBox.SelectedIndex].kbps, App.Viewer.MaxFps));
    }

    private void Scale_Click(object sender, RoutedEventArgs e)
    {
        _oneToOne = !_oneToOne;
        ApplyScaleMode();
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);

    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;
        if (on)
        {
            _prevState = WindowState;
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // necesar ca să acopere bara de activități
            WindowState = WindowState.Maximized;
            Toolbar.Visibility = Visibility.Collapsed;
            HoverStrip.Visibility = Visibility.Visible;
            FullscreenButton.Content = "Ieșire ecran complet";
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _prevState;
            Toolbar.Visibility = Visibility.Visible;
            HoverStrip.Visibility = Visibility.Collapsed;
            FullscreenButton.Content = "Ecran complet";
        }
    }

    private void HoverStrip_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_fullscreen) Toolbar.Visibility = Visibility.Visible;
    }

    private void Toolbar_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_fullscreen && !MonitorBox.IsDropDownOpen && !QualityBox.IsDropDownOpen)
            Toolbar.Visibility = Visibility.Collapsed;
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => Close();

    private void CtrlAltDel_Click(object sender, RoutedEventArgs e) => _session.SendSecureAttention();

    // ---------------- Fișiere ----------------

    private void FilesButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = FilesButton.ContextMenu!;
        menu.PlacementTarget = FilesButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void SendFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Fișiere de trimis pe Desktop-ul de la distanță" };
        if (dlg.ShowDialog(this) == true) _transfers.Upload(dlg.FileNames, null);
    }

    private void SendFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Multiselect = true, Title = "Foldere de trimis pe Desktop-ul de la distanță" };
        if (dlg.ShowDialog(this) == true) _transfers.Upload(dlg.FolderNames, null);
    }

    private void RemoteExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (_explorer is { IsVisible: true })
        {
            _explorer.Activate();
            return;
        }
        _explorer = new RemoteFilesWindow(_session, _transfers) { Owner = this };
        _explorer.Show();
    }

    private void ClipboardMenu_Click(object sender, RoutedEventArgs e)
    {
        if (_session.Clipboard != null) _session.Clipboard.Enabled = ClipboardMenu.IsChecked;
    }

    private void Screen_DragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void Screen_DragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private void Screen_Drop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            _transfers.Upload(paths, null);
        e.Handled = true;
    }

    // ---------------- Mouse ----------------

    private bool TryNormalize(MouseEventArgs e, out ushort x, out ushort y)
    {
        var p = e.GetPosition(Screen);
        double w = Screen.ActualWidth, h = Screen.ActualHeight;
        if (_bitmap == null || w < 1 || h < 1)
        {
            x = y = 0;
            return false;
        }
        x = (ushort)Math.Clamp(Math.Round(p.X / w * 65535), 0, 65535);
        y = (ushort)Math.Clamp(Math.Round(p.Y / h * 65535), 0, 65535);
        return true;
    }

    private void SendMove(MouseEventArgs e)
    {
        if (!TryNormalize(e, out var x, out var y)) return;
        if (x == _lastX && y == _lastY) return;
        _lastX = x;
        _lastY = y;
        _session.SendMouseMove(x, y);
    }

    private void Screen_MouseMove(object sender, MouseEventArgs e) => SendMove(e);

    private static MouseButtonKind? MapButton(MouseButton b) => b switch
    {
        MouseButton.Left => MouseButtonKind.Left,
        MouseButton.Right => MouseButtonKind.Right,
        MouseButton.Middle => MouseButtonKind.Middle,
        MouseButton.XButton1 => MouseButtonKind.X1,
        MouseButton.XButton2 => MouseButtonKind.X2,
        _ => null,
    };

    private void Screen_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MapButton(e.ChangedButton) is not { } button) return;
        SendMove(e);
        _session.SendMouseButton(button, true);
        Screen.CaptureMouse();
        e.Handled = true;
    }

    private void Screen_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (MapButton(e.ChangedButton) is not { } button) return;
        SendMove(e);
        _session.SendMouseButton(button, false);
        if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released &&
            e.MiddleButton == MouseButtonState.Released)
            Screen.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void Screen_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _session.SendMouseWheel(e.Delta, false);
        e.Handled = true;
    }

    // ---------------- Închidere ----------------

    private void OnSessionClosed(string? reason)
    {
        if (_closing) return;
        if (!string.IsNullOrEmpty(reason))
            MessageBox.Show(this, reason, "KodDesk", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private async void OnWindowClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _explorer?.Close();
        foreach (var t in _transfers.Items) t.Cts.Cancel();
        CompositionTarget.Rendering -= OnRendering;
        _statsTimer.Stop();
        _hook.Dispose();
        try { await _session.DisposeAsync(); }
        catch (Exception ex) { Log.Warn("Eroare la închiderea sesiunii", ex); }
    }
}
