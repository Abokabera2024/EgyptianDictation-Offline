using System.IO;
using System.Windows;

namespace EgyptianDictation.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(arg => string.Equals(arg, "--verify", StringComparison.OrdinalIgnoreCase)))
        {
            var reportPath = e.Args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? Path.Combine(Path.GetTempPath(), "EgyptianDictation-installer-verification.txt");
            try
            {
                File.WriteAllText(reportPath, EgyptianDictation.Setup.MainWindow.VerifyEmbeddedPayload());
                Shutdown(0);
            }
            catch (Exception ex)
            {
                File.WriteAllText(reportPath, ex.ToString());
                Shutdown(2);
            }
            return;
        }

        if (e.Args.Any(arg => string.Equals(arg, "--silent", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                // Unattended deployment must opt in explicitly; --silent alone cannot bypass consent.
                var acceptedTerms = e.Args.Any(arg => string.Equals(arg, "--accept-terms", StringComparison.OrdinalIgnoreCase));
                var restartRequired = EgyptianDictation.Setup.MainWindow.Install(
                    EgyptianDictation.Setup.MainWindow.GetInstallRoot(), acceptedTerms);
                Shutdown(restartRequired ? 3010 : 0);
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "EgyptianDictation-setup-error.log"), ex.ToString());
                }
                catch { }
                Shutdown(1);
            }
            return;
        }

        new MainWindow().Show();
    }
}
