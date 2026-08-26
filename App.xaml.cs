using System.Windows;
using System.Windows.Threading;
using KotodamanWordFinder.Services;
using Velopack;
using Velopack.Sources;

namespace KotodamanWordFinder;

public partial class App : Application
{
    // Velopack이 설치/업데이트 적용을 가로챌 수 있도록 반드시 앱 생성보다 먼저 실행되어야 합니다.
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        try
        {
            DataInitializationResult initialization = AppPaths.Initialize();
            AppLog.Initialize();
            AppLog.Info(
                $"데이터 준비 완료 · 사용자 데이터 생성={initialization.CreatedUserData} · " +
                $"기본 파일 복사={initialization.CopiedFileCount} · " +
                $"학습 파일 복사={initialization.CopiedReferenceFileCount} · " +
                $"기존 백업 이전={initialization.CopiedLegacyBackupCount} · " +
                $"신규 캐릭터 병합={initialization.AddedBundledCharacterCount} · " +
                $"데이터 버전={AppPaths.UserDataVersion}");

            if (TryHandleCommandLine(e.Args))
            {
                return;
            }

            BundledDataUpdateResult bundledUpdate = DataUpdateService.ApplyBundledUpdates();
            if (bundledUpdate.HasAppliedUpdates)
            {
                AppLog.Info(
                    $"번들 데이터 업데이트 자동 적용 · " +
                    $"{bundledUpdate.InitialDataVersion} → {bundledUpdate.FinalDataVersion} · " +
                    $"패키지={bundledUpdate.AppliedPackages.Count} · " +
                    $"추가={bundledUpdate.AddedCharacterCount} · 수정={bundledUpdate.UpdatedCharacterCount} · " +
                    $"삭제={bundledUpdate.DeletedCharacterCount} · 이미지={bundledUpdate.AppliedImageCount} · " +
                    $"학습참조={bundledUpdate.AppliedReferenceCount} · " +
                    $"사용자 수정 보존={bundledUpdate.PreservedConflictCount}");
            }

            if (bundledUpdate.HasErrors)
            {
                AppLog.Warning(
                    "일부 번들 데이터 업데이트를 적용하지 못했습니다. " +
                    string.Join(" | ", bundledUpdate.Errors));
            }

            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            _ = CheckForAppUpdatesAsync();
        }
        catch (Exception exception)
        {
            AppLog.Error("프로그램 시작 준비 중 오류가 발생했습니다.", exception);
            MessageBox.Show(
                $"프로그램을 시작하지 못했습니다.\n\n{exception.Message}\n\n" +
                $"로그 폴더: {AppPaths.LogDirectory}",
                "시작 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }


    private bool TryHandleCommandLine(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return false;
        }

        if (!string.Equals(args[0], "--apply-data-update", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Count < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            MessageBox.Show(
                "적용할 데이터 업데이트 ZIP 경로가 없습니다.",
                "데이터 업데이트",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(2);
            return true;
        }

        try
        {
            DataUpdateApplyResult result = DataUpdateService.ApplyPackage(args[1]);
            string conflictText = result.PreservedConflictCount > 0
                ? $"\n사용자 수정 보존: {result.PreservedConflictCount:N0}건"
                : string.Empty;

            MessageBox.Show(
                $"데이터 업데이트를 적용했습니다.\n\n" +
                $"{result.FromDataVersion} → {result.DataVersion}\n" +
                $"캐릭터 추가 {result.AddedCharacterCount:N0}명 · " +
                $"수정 {result.UpdatedCharacterCount:N0}명 · " +
                $"삭제 {result.DeletedCharacterCount:N0}명\n" +
                $"이미지 {result.AppliedImageCount:N0}개{conflictText}\n\n" +
                $"적용 전 백업:\n{result.BackupPath}",
                "데이터 업데이트 완료",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
        }
        catch (Exception exception)
        {
            AppLog.Error("데이터 업데이트 적용 중 오류가 발생했습니다.", exception);
            MessageBox.Show(
                $"데이터 업데이트를 적용하지 못했습니다.\n\n{exception.Message}\n\n" +
                $"로그 폴더: {AppPaths.LogDirectory}",
                "데이터 업데이트 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(3);
        }

        return true;
    }

    /// <summary>
    /// 앱 업데이트 진행을 화면에 알립니다. text 가 null 이면 알릴 것이 없다는 뜻입니다.
    /// App 이 MainWindow 의 컨트롤을 직접 만지지 않도록 이 이벤트 하나만 지납니다.
    /// </summary>
    public static event Action<string?, bool>? UpdateStatusChanged;

    // 다운로드 진행 콜백은 UI 스레드가 아닌 곳에서 옵니다(Velopack 은 IProgress 가 아니라 Action<int>).
    // 구독자가 매번 스레드를 신경 쓰지 않도록 여기서 한 번만 디스패처를 거칩니다.
    private static void ReportUpdateStatus(string? text, bool isError = false)
        => Current?.Dispatcher.BeginInvoke(() => UpdateStatusChanged?.Invoke(text, isError));

    // 공개 저장소를 소스로 쓰므로 토큰 없이 확인 가능합니다(D-2: 코드서명 미구매, GitHub API 60회/시간은
    // 사용자 개인 IP 기준이라 문제되지 않습니다 - PRD FR-6). 실패해도 앱 사용에는 지장이 없어야 하므로
    // 로그를 남기고 넘어가되, 진행 단계는 헤더 상태 칩으로 보여 줍니다 - 예전에는 발견부터 재시작까지
    // 53초 동안 화면에 아무 표시가 없어서 "업데이트가 안 된다"고 오해할 수밖에 없었습니다.
    private static async Task CheckForAppUpdatesAsync()
    {
        try
        {
            var manager = new UpdateManager(
                new GithubSource("https://github.com/sksmsaidkeu/Kotodaman_Simulator", null, false));

            if (!manager.IsInstalled)
            {
                return;
            }

            ReportUpdateStatus("업데이트 확인 중");

            UpdateInfo? updateInfo = await manager.CheckForUpdatesAsync();
            if (updateInfo == null)
            {
                // 알릴 것이 없으면 칩은 데이터 버전으로 돌아갑니다.
                ReportUpdateStatus(null);
                return;
            }

            string newVersion = updateInfo.TargetFullRelease.Version.ToString();
            AppLog.Info($"앱 업데이트 발견 · {newVersion} · 적용할지 사용자에게 묻습니다.");
            ReportUpdateStatus($"새 버전 {newVersion} 발견");

            // 받기 전에 묻는다. 델타를 받아 적용하는 데 1분쯤 걸리는데(실측 49초), 다 받고 나서
            // 물으면 이미 그 시간을 뺏은 뒤라 묻는 의미가 없다.
            MessageBoxResult answer = Application.Current.Dispatcher.Invoke(() =>
                MessageBox.Show(
                    Application.Current.MainWindow,
                    $"새 버전 {newVersion} 이(가) 있습니다. 지금 업데이트할까요?\n\n" +
                    $"현재 버전은 {AppPaths.AppVersion} 입니다.\n\n" +
                    "받는 데 1분쯤 걸리고, 끝나면 프로그램이 저절로 다시 시작합니다.\n" +
                    "'아니오'를 고르면 이번에는 그냥 넘어가고, 다음에 켤 때 다시 물어봅니다.",
                    "업데이트가 있습니다",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question));

            if (answer != MessageBoxResult.Yes)
            {
                AppLog.Info($"사용자가 업데이트를 미뤘습니다 · {newVersion}");
                ReportUpdateStatus($"업데이트 {newVersion} 미룸 · 다음에 켤 때 다시 물어봅니다");
                return;
            }

            AppLog.Info($"앱 업데이트 다운로드를 시작합니다 · {newVersion}");
            ReportUpdateStatus("업데이트 받는 중 0%");

            // 여기가 실측 49초짜리 침묵이었던 구간입니다. 진행률을 그대로 칩에 흘립니다.
            // 같은 값이 여러 번 오므로 바뀔 때만 넘겨 디스패처를 헛돌리지 않습니다.
            int reportedPercent = -1;
            await manager.DownloadUpdatesAsync(updateInfo, percent =>
            {
                if (percent == reportedPercent)
                {
                    return;
                }

                reportedPercent = percent;
                ReportUpdateStatus($"업데이트 받는 중 {percent}%");
            });
            AppLog.Info("앱 업데이트 다운로드 완료 · 정상 종료 후 적용합니다.");
            ReportUpdateStatus($"업데이트 {newVersion} 적용 · 곧 다시 시작합니다");

            // ApplyUpdatesAndRestart는 프로세스를 즉시 종료시켜 Window.Closing(설정 저장 등)을 건너뛴다.
            // WaitExitThenApplyUpdates로 적용을 예약해두고, Shutdown()으로 정상 종료 경로를 태워야
            // MainWindow_Closing의 SaveSettingsImmediatelySafely가 실행된다.
            manager.WaitExitThenApplyUpdates(updateInfo.TargetFullRelease);
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
        catch (Exception exception)
        {
            AppLog.Warning($"앱 자동 업데이트 확인/적용에 실패했습니다: {exception.Message}");
            ReportUpdateStatus("업데이트 실패 · '오류 로그'에서 확인하세요", isError: true);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info("프로그램 종료");
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error("처리되지 않은 UI 오류가 발생했습니다.", e.Exception);
        MessageBox.Show(
            $"예상하지 못한 오류가 발생해 프로그램을 종료합니다.\n\n{e.Exception.Message}\n\n" +
            $"오류 로그: {AppLog.CurrentLogPath}",
            "프로그램 오류",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
        Shutdown(1);
    }

    private static void CurrentDomain_UnhandledException(
        object sender,
        UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLog.Error("처리되지 않은 백그라운드 오류가 발생했습니다.", exception);
        }
        else
        {
            AppLog.Warning($"처리되지 않은 백그라운드 오류: {e.ExceptionObject}");
        }
    }

    private static void TaskScheduler_UnobservedTaskException(
        object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Error("관찰되지 않은 비동기 작업 오류가 발생했습니다.", e.Exception);
        e.SetObserved();
    }
}
