using System.Windows;
using System.Windows.Input;
using KodDesk.Core;
using KodDesk.Core.Clipboard;
using KodDesk.Core.Files;
using KodDesk.Core.Viewer;
using KodDesk.Protocol;

namespace KodDesk.App;

public sealed class RemoteRow(DirItem item)
{
    public DirItem Item { get; } = item;
    public string Name => Item.Name;

    public string Icon => Item.Kind switch
    {
        DirItemKind.Directory => "📁",
        DirItemKind.Drive => "💽",
        DirItemKind.Place => "⭐",
        _ => "📄",
    };

    public string SizeText => Item.Kind switch
    {
        DirItemKind.File => TransferItem.FormatSize(Item.Size),
        DirItemKind.Drive => TransferItem.FormatSize(Item.Size) + " liberi",
        _ => "",
    };

    public string DateText => Item.LastWriteUtcTicks > 0
        ? new DateTime(Item.LastWriteUtcTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm")
        : "";

    public bool IsTransferable => Item.Kind is DirItemKind.File or DirItemKind.Directory;
}

/// <summary>Exploratorul de fișiere de pe calculatorul la distanță.</summary>
public partial class RemoteFilesWindow : Window
{
    private readonly ViewerSession _session;
    private readonly TransferManager _transfers;
    private string _path = "";
    private CancellationTokenSource? _loadCts;
    private Point _dragStart;
    private bool _dragging;

    public RemoteFilesWindow(ViewerSession session, TransferManager transfers)
    {
        InitializeComponent();
        _session = session;
        _transfers = transfers;
        TransfersList.ItemsSource = transfers.Items;
        Title = $"Fișiere pe {session.HostName} — KodDesk";
        Loaded += async (_, _) => await NavigateAsync("");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { _ = NavigateAsync(_path); e.Handled = true; }
        };
    }

    private async Task NavigateAsync(string path)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        StatusText.Text = "Se încarcă...";
        try
        {
            var (normalized, items) = await _session.Files.ListDirectoryAsync(path, cts.Token);
            if (cts.IsCancellationRequested) return;
            _path = normalized;
            PathBox.Text = normalized;
            List.ItemsSource = items.Select(i => new RemoteRow(i)).ToList();
            StatusText.Text = normalized.Length == 0 ? "Locuri și unități" : $"{items.Count} elemente";
            UploadButton.IsEnabled = normalized.Length > 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = "Eroare: " + ex.Message;
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e) => _ = NavigateAsync("");

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = NavigateAsync(_path);

    private void GoUp()
    {
        if (_path.Length == 0) return;
        var trimmed = _path.TrimEnd('\\');
        var parent = trimmed.Length <= 2 ? null : System.IO.Path.GetDirectoryName(trimmed); // rădăcina unei unități -> locuri
        _ = NavigateAsync(parent ?? "");
    }

    private void PathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = NavigateAsync(PathBox.Text.Trim());
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OpenSelected();
        else if (e.Key == Key.Back) GoUp();
    }

    private void OpenSelected()
    {
        if (List.SelectedItem is RemoteRow { Item.Kind: not DirItemKind.File } row)
            _ = NavigateAsync(row.Item.FullPath);
    }

    private List<string> SelectedPaths() =>
        List.SelectedItems.Cast<RemoteRow>().Where(r => r.IsTransferable).Select(r => r.Item.FullPath).ToList();

    // ---------------- descărcare ----------------

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0)
        {
            StatusText.Text = "Selectați fișierele sau folderele de descărcat.";
            return;
        }
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Unde salvez fișierele?",
            InitialDirectory = new CurrentUserContext().KnownFolder(KnownFolders.Downloads) ?? "",
        };
        if (dlg.ShowDialog(this) == true) _transfers.Download(paths, dlg.FolderName);
    }

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(null);

    /// <summary>Tragerea din listă în Explorer-ul local: fișiere virtuale, descărcate pe măsură ce Explorer le copiază.</summary>
    private async void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var paths = SelectedPaths();
        if (paths.Count == 0) return;

        _dragging = true;
        try
        {
            StatusText.Text = "Se pregătește lista de fișiere...";
            var (setId, entries) = await _session.Files.RequestDownloadAsync(paths, CancellationToken.None);
            if (Mouse.LeftButton != MouseButtonState.Pressed)
            {
                StatusText.Text = "Țineți apăsat butonul mouse-ului cât trageți fișierele.";
                return;
            }
            var data = new VirtualFileDataObject(entries, i => new PullStream(_session.Files, setId, i, entries[i]));
            StatusText.Text = OleDrag.Run(data)
                ? "Explorer copiază fișierele (vedeți fereastra lui de progres)."
                : "";
        }
        catch (Exception ex)
        {
            Log.Warn("Tragerea fișierelor a eșuat", ex);
            StatusText.Text = "Eroare: " + ex.Message;
        }
        finally
        {
            _dragging = false;
        }
    }

    // ---------------- încărcare ----------------

    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (_path.Length == 0) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = $"Fișiere de încărcat în {_path}" };
        if (dlg.ShowDialog(this) == true) UploadHere(dlg.FileNames);
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _path.Length > 0 && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (_path.Length > 0 && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            UploadHere(paths);
        e.Handled = true;
    }

    private void UploadHere(IReadOnlyList<string> paths)
    {
        var folder = _path;
        _transfers.Upload(paths, folder, onCompleted: () =>
        {
            if (_path == folder) _ = NavigateAsync(folder);
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _loadCts?.Cancel();
        base.OnClosed(e);
    }
}
