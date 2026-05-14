using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace ChordproExtractor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 28 日以上更新のない demucs_* のみ削除（使用中のキャッシュは温存）
        DemucsWorkCache.DeleteExpiredWorkDirs(DemucsWorkCache.RetentionPeriod);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Debug.WriteLine("UnhandledException: " + ex);
            try
            {
                MessageBox.Show(
                    $"致命的なエラーが発生しました。\n\n{ex.GetType().Name}: {ex.Message}\n\n詳細はデバッグ出力を確認してください。",
                    "ChordproExtractor",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                /* 表示に失敗しても握りつぶす */
            }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Debug.WriteLine("DispatcherUnhandledException: " + e.Exception);
        try
        {
            MessageBox.Show(
                $"未処理の UI 例外:\n\n{e.Exception.Message}\n\n{e.Exception}",
                "ChordproExtractor",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            /* ignore */
        }
        finally
        {
            e.Handled = true;
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        foreach (var x in e.Exception.InnerExceptions)
            Debug.WriteLine("UnobservedTaskException: " + x);

        e.SetObserved();

        try
        {
            var msg = e.Exception.InnerException?.Message ?? e.Exception.Message;
            Current?.Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    try
                    {
                        MessageBox.Show(
                            "バックグラウンド タスクで未処理例外が発生しました。\n\n" + msg,
                            "ChordproExtractor",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                    }
                    catch
                    {
                        /* ignore */
                    }
                }));
        }
        catch
        {
            /* ignore */
        }
    }
}
