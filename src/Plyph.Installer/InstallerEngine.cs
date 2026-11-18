using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace Plyph.Installer;

public sealed class InstallOptions
{
    public string InstallDirectory { get; set; } = DefaultInstallDirectory;
    public bool CreateStartMenuShortcut { get; set; } = true;
    public bool CreateDesktopShortcut { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = true;
    public bool LaunchAfterInstall { get; set; } = true;

    public static string DefaultInstallDirectory
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
                localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local");
            return Path.Combine(localAppData, "Programs", "Plyph");
        }
    }
}

public static class InstallerEngine
{
    public const string UninstallRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Plyph";
    public const string RunRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static async Task InstallAsync(InstallOptions options, IProgress<string>? progress = null)
    {
        var targetDir = Path.GetFullPath(options.InstallDirectory);
        progress?.Report("Stopping any running Plyph instances…");
        StopRunningProcesses();

        progress?.Report($"Creating directory {targetDir}…");
        Directory.CreateDirectory(targetDir);

        var targetExe = Path.Combine(targetDir, "Plyph.exe");
        var uninstallerExe = Path.Combine(targetDir, "Plyph-Uninstall.exe");

        progress?.Report("Extracting Plyph application files…");
        await ExtractPayloadAsync(targetDir, targetExe).ConfigureAwait(false);

        // Copy current setup executable as the uninstaller
        var currentSetupPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(currentSetupPath) && File.Exists(currentSetupPath))
        {
            File.Copy(currentSetupPath, uninstallerExe, overwrite: true);
        }

        if (OperatingSystem.IsWindows())
        {
            if (options.CreateStartMenuShortcut)
            {
                progress?.Report("Creating Start Menu shortcut…");
                var startMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                CreateShortcut(Path.Combine(startMenuDir, "Plyph.lnk"), targetExe, targetDir, "Use AI on selected text anywhere in Windows");
            }

            if (options.CreateDesktopShortcut)
            {
                progress?.Report("Creating Desktop shortcut…");
                var desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                CreateShortcut(Path.Combine(desktopDir, "Plyph.lnk"), targetExe, targetDir, "Use AI on selected text anywhere in Windows");
            }

            progress?.Report("Configuring startup and uninstaller registration…");
            ConfigureStartupRegistry(options.LaunchAtStartup, targetExe);
            RegisterUninstaller(targetDir, targetExe, uninstallerExe);
        }

        progress?.Report("Installation complete.");

        if (options.LaunchAfterInstall && OperatingSystem.IsWindows())
        {
            try
            {
                Process.Start(new ProcessStartInfo(targetExe)
                {
                    WorkingDirectory = targetDir,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }
    }

    public static void Uninstall(bool removeUserData = false, IProgress<string>? progress = null)
    {
        progress?.Report("Stopping running Plyph instances…");
        StopRunningProcesses();

        var installDir = GetRegisteredInstallLocation() ?? InstallOptions.DefaultInstallDirectory;

        if (OperatingSystem.IsWindows())
        {
            progress?.Report("Removing shortcuts and registry entries…");
            var startMenuLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Plyph.lnk");
            var desktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Plyph.lnk");
            TryDeleteFile(startMenuLnk);
            TryDeleteFile(desktopLnk);

            ConfigureStartupRegistry(false, "");
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryKeyPath, throwOnMissingSubKey: false);
            }
            catch
            {
            }
        }

        var targetExe = Path.Combine(installDir, "Plyph.exe");
        TryDeleteFile(targetExe);

        if (removeUserData)
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Plyph");
            if (Directory.Exists(appData))
            {
                try { Directory.Delete(appData, recursive: true); } catch { }
            }
        }

        // Schedule self-deletion of uninstaller and directory after process exit
        if (OperatingSystem.IsWindows() && Directory.Exists(installDir))
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/C ping 127.0.0.1 -n 2 > nul & rmdir /S /Q \"{installDir}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch
            {
            }
        }

        progress?.Report("Uninstallation complete.");
    }

    private static async Task ExtractPayloadAsync(string targetDir, string targetExePath)
    {
        var asm = Assembly.GetExecutingAssembly();
        await using var zipStream = asm.GetManifestResourceStream("Plyph.Payload.zip");
        if (zipStream != null)
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            archive.ExtractToDirectory(targetDir, overwriteFiles: true);
            return;
        }

        await using var resourceStream = asm.GetManifestResourceStream("Plyph.Payload.exe");
        if (resourceStream != null)
        {
            await using var fs = new FileStream(targetExePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await resourceStream.CopyToAsync(fs).ConfigureAwait(false);
            return;
        }

        var adjacent = Path.Combine(AppContext.BaseDirectory, "Plyph.exe");
        if (File.Exists(adjacent))
        {
            File.Copy(adjacent, targetExePath, overwrite: true);
            return;
        }

        throw new FileNotFoundException("Installer payload (Plyph.Payload.zip / Plyph.exe) was not found.");
    }

    private static void StopRunningProcesses()
    {
        foreach (var name in new[] { "Plyph", "Plyph-Portable" })
        {
            try
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (proc.Id != Environment.ProcessId)
                        {
                            proc.Kill();
                            proc.WaitForExit(2000);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }
    }

    private static void CreateShortcut(string shortcutPath, string targetExe, string workingDir, string description)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return;
            var shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return;
            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                null,
                shell,
                new object[] { shortcutPath });
            if (shortcut == null)
                return;

            var scType = shortcut.GetType();
            scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe });
            scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
            scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
            scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { $"{targetExe},0" });
            scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        catch
        {
        }
    }

    private static void ConfigureStartupRegistry(bool enable, string targetExe)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, writable: true);
            if (key == null)
                return;
            if (enable && !string.IsNullOrEmpty(targetExe))
                key.SetValue("Plyph", $"\"{targetExe}\"");
            else
                key.DeleteValue("Plyph", throwOnMissingValue: false);
        }
        catch
        {
        }
    }

    private static void RegisterUninstaller(string installDir, string targetExe, string uninstallerExe)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryKeyPath);
            if (key == null)
                return;
            key.SetValue("DisplayName", "Plyph");
            key.SetValue("DisplayVersion", "1.0.0");
            key.SetValue("Publisher", "ubai.dev");
            key.SetValue("DisplayIcon", targetExe);
            key.SetValue("InstallLocation", installDir);
            key.SetValue("UninstallString", $"\"{uninstallerExe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{uninstallerExe}\" --uninstall --silent");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch
        {
        }
    }

    private static string? GetRegisteredInstallLocation()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryKeyPath, writable: false);
            return key?.GetValue("InstallLocation") as string;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
