using System.Diagnostics;
using System.Windows;
using H.NotifyIcon;
using MeetingAssistant.App.Infrastructure;
using MeetingAssistant.App.Shell;
using MeetingAssistant.App.Themes;
using MeetingAssistant.App.ViewModels;
using MeetingAssistant.Core;

namespace MeetingAssistant.App;

/// App startup and window management: single instance, app data folder, theme, main window,
/// notification-area icon, Settings, the floating transcript (reopened if it was open at quit) and the
/// dictation HUD. Launch argument `-select <sessionId>` opens that session (spec 01).
public partial class App : Application, IWindowService
{
    SingleInstance? _single;
    MainWindow? _main;
    SettingsWindow? _settings;
    FloatingTranscriptWindow? _floating;
    DictationHudWindow? _hud;
    TaskbarIcon? _tray;
    bool _quitting;

    /// The running app.
    public static new App Current => (App)Application.Current;

    /// UI state store (see <see cref="IUiState"/>); replace before startup to back it with the Core preferences.
    public IUiState UiState { get; set; } = new MemoryUiState();

    public ShellViewModel Shell { get; private set; } = null!;
    public FloatingTranscriptViewModel Floating { get; private set; } = null!;
    /// The floating window's view model for XAML outside its window (tray menu: ghost mode).
    public static FloatingTranscriptViewModel FloatingModel => Current.Floating;
    public HudViewModel Hud { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            _single.SignalFirst();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) =>
        {
            Debug.WriteLine(ex.Exception);
            ex.Handled = true;
            if (Shell is null || _main is null)
            {
                // Failed before the main window exists: say so and exit instead of lingering without a window
                // (ShutdownMode is explicit, and the single-instance mutex would block the next launch).
                MessageBox.Show($"MeetingAssistant could not start: {ex.Exception.Message}", "MeetingAssistant",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
                return;
            }
            // Keep the app (and a running recording) alive; show the error like the Mac's global alert.
            Shell.ErrorMessage = ex.Exception.Message;
        };
        // Exceptions of fire-and-forget tasks must not go unnoticed (or tear the app down).
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Debug.WriteLine(ex.Exception);
            ex.SetObserved();
        };

        AppPaths.Ensure();
        ConfigureServices();          // App.Scribe.cs: preferences-backed UiState and shared services
        ThemeManager.Initialize();

        Shell = new ShellViewModel(UiState, this);
        Floating = new FloatingTranscriptViewModel(UiState, Shell);
        Hud = new HudViewModel();

        _main = new MainWindow(UiState) { DataContext = Shell };
        var select = SelectArgument(e.Args);
        if (select is not null)
        {
            Shell.Mode = AppMode.Scribe;
            Shell.Scribe.Select(select);
        }
        _main.Show();

        _tray = new TrayIcon { DataContext = Shell };
        // Fails when the taskbar is not up yet (autostart at sign-in): the app must still start.
        try { _tray.ForceCreate(false); }
        catch (Exception ex) { Debug.WriteLine($"Tray icon unavailable: {ex.Message}"); }

        _hud = new DictationHudWindow(Hud);

        if (UiState.Get(UiKeys.FloatingOpen, false)) ToggleFloating();

        // Each area starts on its own: a failure in one (no microphone, broken hook, Aya…) is reported
        // and must not keep the others from starting.
        StartArea("recording", StartScribe);      // App.Scribe.cs: recording, queue, launch recovery, floating window
        StartArea("Assist", StartAssist);         // App.Assist.cs: Assist controller and views
        StartArea("dictation", StartDictation);   // App.Dictation.cs: hotkeys, HUD, Scribe+
        _single.Listen(() => Dispatcher.BeginInvoke(ShowMain));
    }

    void StartArea(string name, Action start)
    {
        try { start(); }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            Shell.ErrorMessage = $"Could not start {name}: {ex.Message}";
        }
    }

    // Feature areas plug in here; each lives in its own partial file.
    partial void ConfigureServices();
    partial void StartScribe();
    partial void StartAssist();
    partial void StartDictation();
    partial void StopScribe();
    partial void StopAssist();
    partial void StopDictation();

    /// `-select <sessionId>`.
    static string? SelectArgument(string[] args)
    {
        var i = Array.IndexOf(args, "-select");
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // ---- IWindowService ----

    public void ShowMain()
    {
        if (_main is null) return;
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        // Bring to the front even when another app is active.
        _main.Topmost = true;
        _main.Topmost = false;
        _main.Focus();
    }

    public void ShowSettings(string? tab = null)
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            if (_main is { IsVisible: true }) _settings.Owner = _main;
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        _settings.SelectTab(tab);
        _settings.Activate();
    }

    public bool IsFloatingOpen => _floating is not null;

    public void ToggleFloating()
    {
        if (_floating is not null) { _floating.Close(); return; }
        _floating = new FloatingTranscriptWindow(UiState, Floating);
        _floating.Closed += (_, _) => { _floating = null; FloatingClosed?.Invoke(); };
        UiState.Set(UiKeys.FloatingOpen, true);
        _floating.Show();
        FloatingOpened?.Invoke();
    }

    /// The floating window opened (start listening) or closed (stop listening); the listening controller subscribes.
    public event Action? FloatingOpened;
    public event Action? FloatingClosed;

    public int ShowAlert(string title, string message, params AlertButton[] buttons)
    {
        if (buttons.Length == 0) buttons = [new AlertButton("OK", AlertButtonKind.Primary, IsDefault: true, IsCancel: true)];
        var w = new AlertWindow(title, "", message, buttons);
        var owner = _settings is { IsActive: true } ? (Window)_settings : _main;
        if (owner is { IsVisible: true }) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
        return w.Result;
    }

    /// Alert with a bold heading above the message (delete confirmations: "Delete “…”?").
    public int ShowConfirm(string heading, string message, params AlertButton[] buttons)
    {
        var w = new AlertWindow("MeetingAssistant", heading, message, buttons);
        if (_main is { IsVisible: true }) w.Owner = _main; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.ShowDialog();
        return w.Result;
    }

    public void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"OpenUrl failed: {ex.Message}"); }
    }

    /// Raised before the app quits (flush drafts, stop recordings); the feature phase subscribes.
    public event Action? Quitting;

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        Quitting?.Invoke();
        if (_floating is not null) { _floating.AppQuitting = true; _floating.Close(); }
        _settings?.Close();
        _hud?.Close();
        if (_main is not null) { _main.AllowClose = true; _main.Close(); }
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopAssist();
        StopDictation();
        StopScribe();
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
