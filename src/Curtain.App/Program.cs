using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;
using Velopack;

namespace Curtain.App;

internal static class Program
{
    internal static bool StartInBackground => Environment.GetCommandLineArgs()
        .Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));

    [STAThread]
    private static int Main(string[] args)
    {
        // Velopack must handle installer/update hooks before WinUI and single-instance setup.
        // Prevent an installer hook from starting a tray process or opening UI.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .Run();
        ComWrappersSupport.InitializeComWrappers();
        AppInstance mainInstance = AppInstance.FindOrRegisterForKey("Curtain.Main");
        if (!mainInstance.IsCurrent)
        {
            AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            mainInstance.RedirectActivationToAsync(activation).AsTask().GetAwaiter().GetResult();
            return 0;
        }

        mainInstance.Activated += OnRedirectedActivation;
        Application.Start(initializationParameters =>
        {
            DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            new App();
        });
        return 0;
    }

    private static void OnRedirectedActivation(object? sender, AppActivationArguments args) =>
        App.RequestMainWindowActivation();
}
