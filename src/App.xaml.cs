using System.Windows;
using System.Windows.Threading;
using iTask.ShellIntegration;
using iTask.Utilities;

namespace iTask;

/// <summary>
/// Command line:
///   iTask.exe                     run
///   iTask.exe --quit              ask the running instance to exit cleanly
///   iTask.exe --restore-taskbar   stop iTask (if running) and force the native taskbar back
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\iTask.SingleInstance";
    private const string QuitEventName = @"Local\iTask.Quit";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _quitEvent;
    private RegisteredWaitHandle? _quitWait;
    private AppHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToHashSet();

        if (args.Contains("--quit") || args.Contains("--restore-taskbar"))
        {
            bool wasRunning = SignalRunningInstanceToQuit();
            if (args.Contains("--restore-taskbar"))
                TaskbarController.RecoverFromPreviousSession();
            Log.Info($"Command line {string.Join(' ', e.Args)} handled (instance was running: {wasRunning}).");
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            Log.Info("Another instance is already running; exiting.");
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        RegisterCrashHandlers();
        ListenForQuitSignal();

        _host = new AppHost();
        try
        {
            _host.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            _host.Dispose();
            _host = null;
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _quitWait?.Unregister(null);
        _host?.Dispose();
        _host = null;
        _quitEvent?.Dispose();
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Logging off / shutting down: put the taskbar setting back before we are torn down.
        _host?.Dispose();
        _host = null;
        base.OnSessionEnding(e);
    }

    private void ListenForQuitSignal()
    {
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent,
            (_, _) => Dispatcher.BeginInvoke(() => Shutdown()), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private static bool SignalRunningInstanceToQuit()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
            return false;
        using (quit)
            quit.Set();

        // Wait (bounded) for the instance to release its mutex so the restore below doesn't race it.
        try
        {
            using var mutex = new Mutex(false, InstanceMutexName);
            if (mutex.WaitOne(TimeSpan.FromSeconds(5)))
                mutex.ReleaseMutex();
        }
        catch (AbandonedMutexException)
        {
            // Previous owner died; nothing to wait for.
        }
        return true;
    }

    private void RegisterCrashHandlers()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            // Keep the shell alive for recoverable UI errors instead of leaving the user with no taskbar.
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Fatal unhandled exception", e.ExceptionObject as Exception);
            _host?.EmergencyRestore();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => _host?.EmergencyRestore();

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }
}
