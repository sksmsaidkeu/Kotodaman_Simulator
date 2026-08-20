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
        Title = $"{Title} v{AppPaths.AppVersion}";
        _dataDirectory = dataDirectory;
        BackupDirectoryText.Text = DataBackupService.GetBackupDirectory(_dataDirectory);
        RefreshBackupList();
    }

    public bool RestoreCompleted { get; private set; }

    private void RefreshBackupList(string? selectPath = null)
    {
        IReadOnlyList<BackupArchiveInfo> backups = DataBackupService.ListBackups(_dataDirectory);
        BackupListBox.ItemsSource = backups;
        BackupCountText.Text = $"{backups.Count:N0}개";

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
            "현재 데이터를 백업하는 중입니다. 캐릭터 이미지가 많으면 잠시 걸릴 수 있습니다.",
            async () =>
            {
                string backupPath = await Task.Run(
                    () => DataBackupService.CreateManualBackup(_dataDirectory));
                RefreshBackupList(backupPath);
                StatusText.Text =
                    $"백업 완료 · {Path.GetFileName(backupPath)} · " +
                    DataBackupService.FormatByteSize(new FileInfo(backupPath).Length);
                StatusText.Foreground = BrushFromHex("#8FE3B1");
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
            ShowError("복원할 백업을 먼저 선택하세요.");
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            $"다음 백업으로 현재 데이터를 되돌릴까요?\n\n" +
            $"{selected.FileName}\n" +
            $"{selected.CreatedText} · {selected.SizeText}\n\n" +
            "복원 직전에 현재 데이터도 자동으로 안전 백업합니다.",
            "데이터 백업 복원",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunBusyAsync(
            "백업을 복원하는 중입니다. 현재 상태 안전 백업 → 데이터 교체 순서로 진행합니다.",
            async () =>
            {
                BackupRestoreResult result = await Task.Run(
                    () => DataBackupService.RestoreBackup(_dataDirectory, selected.Path));

                RestoreCompleted = true;
                StatusText.Text =
                    $"복원 완료 · 복원 전 상태도 {Path.GetFileName(result.SafetyBackupPath)} 로 안전 백업했습니다.";
                StatusText.Foreground = BrushFromHex("#8FE3B1");

                MessageBox.Show(
                    "복원이 완료되었습니다.\n현재 창을 닫으면 메인 화면도 복원된 데이터를 다시 읽습니다.",
                    "복원 완료",
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
            ShowError("삭제할 백업을 먼저 선택하세요.");
            return;
        }

        MessageBoxResult answer = MessageBox.Show(
            $"이 백업 파일을 삭제할까요?\n\n{selected.FileName}\n{selected.SizeText}",
            "백업 삭제",
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
            StatusText.Text = $"'{selected.FileName}' 백업을 삭제했습니다.";
            StatusText.Foreground = BrushFromHex("#FFD08A");
        }
        catch (Exception exception)
        {
            ShowError($"백업 삭제에 실패했습니다.\n\n{exception.Message}");
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
            ShowError($"백업 폴더를 열 수 없습니다.\n\n{exception.Message}");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private async Task RunBusyAsync(string message, Func<Task> action)
    {
        SetBusy(true);
        StatusText.Text = message;
        StatusText.Foreground = BrushFromHex("#AEB8C8");

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
        StatusText.Foreground = BrushFromHex("#FF9B9B");

        MessageBox.Show(
            message,
            "백업 오류",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static System.Windows.Media.SolidColorBrush BrushFromHex(string hex)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
}
