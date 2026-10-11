using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using KodDesk.Core;
using KodDesk.Core.Files;
using KodDesk.Core.Viewer;

namespace KodDesk.App;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
}

/// <summary>Un transfer afișat în panoul de progres.</summary>
public sealed class TransferItem : INotifyPropertyChanged
{
    private string _status = "Se pregătește...";
    private double _percent;
    private bool _indeterminate = true;
    private bool _active = true;
    private bool _failed;
    private string? _folder;

    public TransferItem(string title, Action<TransferItem> remove)
    {
        Title = title;
        CancelCommand = new RelayCommand(() => Cts.Cancel(), () => _active);
        CloseCommand = new RelayCommand(() => remove(this), () => !_active);
        OpenFolderCommand = new RelayCommand(
            () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true }),
            () => _folder != null);
    }

    public string Title { get; }
    public CancellationTokenSource Cts { get; } = new();
    public ICommand CancelCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand OpenFolderCommand { get; }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public double Percent { get => _percent; private set => Set(ref _percent, value); }
    public bool Indeterminate { get => _indeterminate; private set => Set(ref _indeterminate, value); }
    public bool Active { get => _active; private set => Set(ref _active, value); }
    public bool Failed { get => _failed; private set => Set(ref _failed, value); }
    public bool HasFolder => _folder != null;

    public void Report(TransferProgress p)
    {
        Indeterminate = p.Total <= 0;
        Percent = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        Status = p.Total > 0 ? $"{FormatSize(p.Done)} din {FormatSize(p.Total)}" : "Se transferă...";
    }

    public void Finish(bool ok, string message, string? localFolder = null)
    {
        Active = false;
        Failed = !ok;
        Indeterminate = false;
        Percent = ok ? 100 : Percent;
        Status = message;
        _folder = ok ? localFolder : null;
        OnPropertyChanged(nameof(HasFolder));
        CommandManager.InvalidateRequerySuggested();
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Transferurile unei sesiuni (folosite de fereastra de control și de exploratorul la distanță).</summary>
public sealed class TransferManager(ViewerSession session)
{
    public ObservableCollection<TransferItem> Items { get; } = [];

    private void Remove(TransferItem item) => Items.Remove(item);

    private static string Describe(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? System.IO.Path.GetFileName(paths[0].TrimEnd('\\')) : $"{paths.Count} elemente";

    /// <summary>Trimite fișiere locale; <paramref name="remoteFolder"/> null = Desktop-ul de la distanță.</summary>
    public async void Upload(IReadOnlyList<string> localPaths, string? remoteFolder, Action? onCompleted = null)
    {
        var item = new TransferItem($"↑ {Describe(localPaths)}", Remove);
        Items.Add(item);
        try
        {
            var result = await session.Files.UploadAsync(localPaths, remoteFolder, new Progress<TransferProgress>(item.Report), item.Cts.Token);
            item.Finish(result.Ok, result.Ok ? $"Salvat în {result.SavedTo}" : result.Message);
            if (result.Ok)
            {
                onCompleted?.Invoke();
                AutoRemove(item);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Trimiterea fișierelor a eșuat", ex);
            item.Finish(false, ex.Message);
        }
    }

    /// <summary>Descarcă fișiere/foldere de la distanță în <paramref name="localFolder"/>.</summary>
    public async void Download(IReadOnlyList<string> remotePaths, string localFolder)
    {
        var item = new TransferItem($"↓ {Describe(remotePaths)}", Remove);
        Items.Add(item);
        try
        {
            var (setId, entries) = await session.Files.RequestDownloadAsync(remotePaths, item.Cts.Token);
            await session.Files.ReceiveSetAsync(setId, entries, localFolder, new Progress<TransferProgress>(item.Report), item.Cts.Token);
            item.Finish(true, $"Salvat în {localFolder}", localFolder);
        }
        catch (OperationCanceledException)
        {
            item.Finish(false, "Transfer anulat.");
        }
        catch (Exception ex)
        {
            Log.Warn("Descărcarea a eșuat", ex);
            item.Finish(false, ex.Message);
        }
    }

    private async void AutoRemove(TransferItem item)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        Items.Remove(item);
    }
}
