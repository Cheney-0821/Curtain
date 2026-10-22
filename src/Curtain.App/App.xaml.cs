using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Curtain.App;

public partial class App : Application
{
    private static MainWindow? _mainWindow;
    private static DispatcherQueue? _dispatcherQueue;
    private static int _activationPending;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _mainWindow = new MainWindow();
        bool activationPending = Interlocked.Exchange(ref _activationPending, 0) != 0;
        if (Program.StartInBackground && !activationPending) _mainWindow.StartHidden();
        else _mainWindow.Activate();
    }

    internal static void RequestMainWindowActivation()
    {
        var dispatcher = _dispatcherQueue;
        if (dispatcher is null)
        {
            Interlocked.Exchange(ref _activationPending, 1);
            return;
        }
        dispatcher.TryEnqueue(() => _mainWindow?.ShowFromTray());
    }

    internal static void OnMainWindowClosed(MainWindow window)
    {
        if (ReferenceEquals(_mainWindow, window)) _mainWindow = null;
    }
}
