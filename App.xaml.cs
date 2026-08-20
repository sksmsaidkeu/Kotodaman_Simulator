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

    // 공개 저장소를 소스로 쓰므로 토큰 없이 확인 가능합니다(D-2: 코드서명 미구매, GitHub API 60회/시간은
    // 사용자 개인 IP 기준이라 문제되지 않습니다 - PRD FR-6). 실패해도 앱 사용에는 지장이 없어야 하므로
    // 조용히 로그만 남기고 넘어갑니다.
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

            UpdateInfo? updateInfo = await manager.CheckForUpdatesAsync();
            if (updateInfo == null)
            {
                return;
            }

            AppLog.Info($"앱 업데이트 발견 · {updateInfo.TargetFullRelease.Version} · 다운로드를 시작합니다.");
            await manager.DownloadUpdatesAsync(updateInfo);
            AppLog.Info("앱 업데이트 다운로드 완료 · 재시작하여 적용합니다.");
            manager.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception exception)
        {
            AppLog.Warning($"앱 자동 업데이트 확인/적용에 실패했습니다: {exception.Message}");
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
