using System.Windows.Forms;

namespace Plyph.Installer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var isUninstall = args.Any(a =>
            string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/U", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/uninstall", StringComparison.OrdinalIgnoreCase));

        var isSilent = args.Any(a =>
            string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/quiet", StringComparison.OrdinalIgnoreCase));

        if (isSilent)
        {
            try
            {
                if (isUninstall)
                {
                    InstallerEngine.Uninstall(removeUserData: false);
                }
                else
                {
                    InstallerEngine.InstallAsync(new InstallOptions
                    {
                        LaunchAfterInstall = false
                    }).GetAwaiter().GetResult();
                }
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new InstallerForm(startInUninstallMode: isUninstall));
        return 0;
    }
}
