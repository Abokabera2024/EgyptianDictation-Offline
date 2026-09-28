using System.Windows;

namespace EgyptianDictation.Host;

public partial class App : Application
{
    private MicrophoneDictationController? _headlessController;
    private HostPipeBridge? _headlessBridge;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(arg => string.Equals(arg, "--headless", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _headlessController = new MicrophoneDictationController();
            _headlessBridge = new HostPipeBridge(_headlessController);
            return;
        }

        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_headlessBridge is not null)
            _headlessBridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
        if (_headlessController is not null)
            _headlessController.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnExit(e);
    }
}
