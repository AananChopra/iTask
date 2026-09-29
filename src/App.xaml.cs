using System.Diagnostics;
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
///   iTask.exe --restarted         (internal) started by a self-restart; waits for the old instance
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = @"Local\iTask.SingleInstance";
    private const string QuitEventName = @"Local\iTask.Quit";
    private const string RestartedArg = "--restarted";

    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly Queue<DateTime> _recentErrors = new();
    private Mutex? _instanceMutex;
    private EventWaitHandle? _quitEvent;
    private RegisteredWaitHandle? _quitWait;
    private AppHost? _host;
    private HealthMonitor? _health;
    private bool _isRestart;
    private bool _restarting;
    private bool _restartDeclined;

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

        _isRestart = args.Contains(RestartedArg);
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool createdNew);
        // After a self-restart the old instance may still be on its way out; wait for it.
        if (!createdNew && _isRestart)
            createdNew = WaitForMutex(_instanceMutex);
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
            return;
        }

        _health = new HealthMonitor();
        _health.Unhealthy += (_, reason) => Restart(reason);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _health?.Dispose();
        _quitWait?.Unregister(null);
        _host?.Dispose();
        _host = null;
        _quitEvent?.Dispose();
        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        if (_restarting)
        {
            try { Process.Start(Environment.ProcessPath!, RestartedArg); }
            catch (Exception ex) { Log.Error("Relaunch after restart failed", ex); }
        }
        base.OnExit(e);
    }

    /// <summary>
    /// Shuts down cleanly (the taskbar comes back for a moment) and starts a fresh instance. Used
    /// when WPF can no longer render, which it never recovers from on its own.
    /// </summary>
    private void Restart(string reason)
    {
        if (_restarting)
            return;
        // A fresh instance that breaks again straight away would only flicker in a loop; stay put.
        if (_isRestart && DateTime.UtcNow - _startedAt < TimeSpan.FromMinutes(2))
        {
            if (!_restartDeclined)
                Log.Error($"Restart wanted ({reason}) but iTask only just restarted; not restarting again yet.");
            _restartDeclined = true;
            return;
        }
        _restarting = true;
        Log.Error($"Restarting iTask: {reason}.");
        Dispatcher.BeginInvoke(() => Shutdown());
    }

    private static bool WaitForMutex(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException)
        {
            return true; // previous owner died; the mutex is ours now
        }
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

            // WPF reports a dead render target (usually GDI exhaustion) as OutOfMemoryException, and
            // after that nothing draws or animates again. A flood of errors means the same thing.
            if (e.Exception is OutOfMemoryException)
                Restart("rendering failed (OutOfMemoryException)");
            else if (RecordErrorBurst())
                Restart("too many UI errors in a row");
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

    /// <summary>Notes one UI error; true once 20 have landed within 10 seconds.</summary>
    private bool RecordErrorBurst()
    {
        var now = DateTime.UtcNow;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > TimeSpan.FromSeconds(10))
            _recentErrors.Dequeue();
        return _recentErrors.Count >= 20;
    }
}
