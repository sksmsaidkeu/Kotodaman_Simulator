using KotodamanWordFinder.Themes;
using System.Diagnostics;
using System.IO;
using System.Windows;
using KotodamanWordFinder.Services;

namespace KotodamanWordFinder;

public partial class DataBackupWindow : Window
{
    private readonly string _dataDirectory;
    private bool _isBusy;

    public DataBackupWindow(string dataDirectory)
    {
        InitializeComponent();
        _dataDirectory = dataDirectory;
        BackupDirectoryText.Text = DataBackupService.GetBackupDirectory(_dataDirectory);
        RefreshBackupList();
    }

    public bool RestoreCompleted { get; private set; }

    private void RefreshBackupList(string? selectPath = null)
    {
        IReadOnlyList<BackupArchiveInfo> backups = DataBackupService.ListBackups(_dataDirectory);
        BackupListBox.ItemsSource = backups;
        BackupCountText.Text = string.Format(Loc.Get("Str.Backup.CountFormat"), backups.Count.ToString("N0"));

        BackupArchiveInfo? selected = !string.IsNullOrWhiteSpace(selectPath)
            ? backups.FirstOrDefault(item =>
                string.Equals(item.Path, selectPath, StringComparison.OrdinalIgnoreCase))
            : backups.FirstOrDefault();

        BackupListBox.SelectedItem = selected;
    }

    private async void CreateBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        await RunBusyAsync(
            Loc.Get("Str.Backup.BusyBackingUp"),
            async () =>
            {
                string backupPath = await Task.Run(
                    () => DataBackupService.CreateManualBackup(_dataDirectory));
                RefreshBackupList(backupPath);
                StatusText.Text = string.Format(
                    Loc.Get("Str.Backup.CompleteStatus"),
                    Path.GetFileName(backupPath),
                    DataBackupService.FormatByteSize(new FileInfo(backupPath).Length));
                StatusText.Foreground = Theme.Success;
            });
    }

    private async void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (BackupListBox.SelectedItem is not BackupArchiveInfo selected)
        {
            ShowError(Loc.Get("Str.Backup.SelectToRestore"));
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            string.Format(
                Loc.Get("Str.Backup.ConfirmRestore"),
                selected.FileName,
                selected.CreatedText,
                selected.SizeText),
            Loc.Get("Str.Backup.ConfirmRestoreTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync(
            Loc.Get("Str.Backup.BusyRestoring"),
            async () =>
            {
                BackupRestoreResult result = await Task.Run(
                    () => DataBackupService.RestoreBackup(_dataDirectory, selected.Path));

                RestoreCompleted = true;
                StatusText.Text = string.Format(
                    Loc.Get("Str.Backup.RestoreCompleteStatus"),
                    Path.GetFileName(result.SafetyBackupPath));
                StatusText.Foreground = Theme.Success;

                MessageBox.Show(
                    Loc.Get("Str.Backup.RestoreCompleteBody"),
                    Loc.Get("Str.Backup.RestoreCompleteTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
            });
    }

    private void DeleteBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (BackupListBox.SelectedItem is not BackupArchiveInfo selected)
        {
            ShowError(Loc.Get("Str.Backup.SelectToDelete"));
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            string.Format(Loc.Get("Str.Backup.ConfirmDelete"), selected.FileName, selected.SizeText),
            Loc.Get("Str.Backup.ConfirmDeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            File.Delete(selected.Path);
            RefreshBackupList();
            StatusText.Text = string.Format(Loc.Get("Str.Backup.DeletedStatus"), selected.FileName);
            StatusText.Foreground = Theme.Warn;
        }
        catch (Exception exception)
        {
            ShowError(string.Format(Loc.Get("Str.Backup.DeleteFailed"), exception.Message));
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
        => RefreshBackupList();

    private void OpenBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = DataBackupService.GetBackupDirectory(_dataDirectory);
            Directory.CreateDirectory(directory);

            Process.Start(new ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowError(string.Format(Loc.Get("Str.Backup.OpenFolderFailed"), exception.Message));
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        SetBusy(true);
        StatusText.Text = message;
        StatusText.Foreground = Theme.TextSecondary;

        try
        {
            await action();
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        CreateBackupButton.IsEnabled = !busy;
        RestoreBackupButton.IsEnabled = !busy;
        DeleteBackupButton.IsEnabled = !busy;
        BackupListBox.IsEnabled = !busy;
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = Theme.Error;

        MessageBox.Show(
            message,
            Loc.Get("Str.Backup.ErrorTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

}
