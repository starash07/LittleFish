using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LittleFish.Uninstaller;

internal static class Program
{
    private const string AppName = "LittleFish";
    private const string AppExeName = "LittleFish.exe";
    private const string UninstallerExeName = "LittleFish_Uninstall.exe";
    private const string ManifestFileName = ".littlefish-manifest.txt";
    private const string SettingsFileName = "settings.json";
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\LittleFish";
    private const uint MbOk = 0x00000000;
    private const uint MbYesNo = 0x00000004;
    private const uint MbIconInformation = 0x00000040;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbDefaultButton2 = 0x00000100;
    private const int IdYes = 6;
    private const uint MoveFileDelayUntilReboot = 0x00000004;

    private enum InstallScope
    {
        User,
        Machine
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(argument => string.Equals(argument, "--package-smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        try
        {
            var installDirectory = GetArgumentValue(args, "--uninstall");
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                ShowMessage("未找到 LittleFish 安装位置，无法开始卸载。", MbIconWarning);
                return 1;
            }

            installDirectory = Path.GetFullPath(installDirectory);
            var installScope = GetInstallScope(args);
            var isTemporaryCopy = args.Any(argument => string.Equals(argument, "--temp", StringComparison.OrdinalIgnoreCase));
            var quiet = args.Any(argument => string.Equals(argument, "--quiet", StringComparison.OrdinalIgnoreCase));
            var requestedDeleteSettings = string.Equals(GetArgumentValue(args, "--delete-settings"), "1", StringComparison.Ordinal);
            if (!isTemporaryCopy)
            {
                return ConfirmAndRelaunchFromTemp(installDirectory, installScope, quiet, requestedDeleteSettings);
            }

            if (int.TryParse(GetArgumentValue(args, "--parent"), out var parentId))
            {
                WaitForParent(parentId);
            }

            var deleteSettings = string.Equals(GetArgumentValue(args, "--delete-settings"), "1", StringComparison.Ordinal);
            Uninstall(installDirectory, installScope, deleteSettings);
            if (!quiet)
            {
                ShowMessage("LittleFish 已卸载完成。", MbIconInformation);
            }
            ScheduleTemporaryCopyCleanup();
            return 0;
        }
        catch (Exception exception)
        {
            ShowMessage($"LittleFish 卸载没有完成。\n\n{exception.Message}", MbIconWarning);
            return 1;
        }
    }

    private static int ConfirmAndRelaunchFromTemp(
        string installDirectory,
        InstallScope installScope,
        bool quiet,
        bool requestedDeleteSettings)
    {
        ValidateInstallDirectory(installDirectory);
        if (!quiet)
        {
            var confirmation = MessageBoxW(
                IntPtr.Zero,
                "确定要卸载 LittleFish 吗？\n\n默认会保留阅读设置、进度和书签。",
                "卸载 LittleFish",
                MbYesNo | MbIconWarning | MbDefaultButton2);
            if (confirmation != IdYes)
            {
                return 0;
            }
        }

        var deleteSettings = installScope == InstallScope.User && requestedDeleteSettings;
        if (!quiet && installScope == InstallScope.User)
        {
            var deleteChoice = MessageBoxW(
                IntPtr.Zero,
                "是否同时删除阅读设置、进度和书签？\n\n选择“否”将保留 settings.json。",
                "卸载 LittleFish",
                MbYesNo | MbIconWarning | MbDefaultButton2);
            deleteSettings = deleteChoice == IdYes;
        }

        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位 LittleFish 卸载程序。");
        var tempDirectory = Path.Combine(Path.GetTempPath(), "LittleFish.Uninstall", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var tempExecutable = Path.Combine(tempDirectory, UninstallerExeName);
        File.Copy(currentExecutable, tempExecutable, overwrite: true);

        var startInfo = new ProcessStartInfo(tempExecutable)
        {
            UseShellExecute = true
        };
        if (installScope == InstallScope.Machine)
        {
            startInfo.Verb = "runas";
        }
        startInfo.ArgumentList.Add("--temp");
        startInfo.ArgumentList.Add("--uninstall");
        startInfo.ArgumentList.Add(installDirectory);
        startInfo.ArgumentList.Add("--scope");
        startInfo.ArgumentList.Add(installScope == InstallScope.Machine ? "machine" : "user");
        startInfo.ArgumentList.Add("--delete-settings");
        startInfo.ArgumentList.Add(deleteSettings ? "1" : "0");
        if (quiet)
        {
            startInfo.ArgumentList.Add("--quiet");
        }
        startInfo.ArgumentList.Add("--parent");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        try
        {
            Process.Start(startInfo);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return 0;
        }
        return 0;
    }

    private static void Uninstall(string installDirectory, InstallScope installScope, bool deleteSettings)
    {
        ValidateInstallDirectory(installDirectory);
        CloseInstalledProcesses(installDirectory);
        DeleteShortcuts(installScope);
        DeleteUninstallRegistryEntry(installScope);
        DeleteManagedFiles(installDirectory);

        if (deleteSettings)
        {
            TryDeleteFile(Path.Combine(installDirectory, SettingsFileName));
            TryDeleteFile(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName,
                SettingsFileName));
            TryDeleteDirectory(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName), recursive: false);
        }

        TryDeleteDirectory(installDirectory, recursive: false);
    }

    private static void ValidateInstallDirectory(string installDirectory)
    {
        var root = Path.GetPathRoot(installDirectory);
        if (string.IsNullOrWhiteSpace(root)
            || string.Equals(
                installDirectory.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("卸载目录无效。");
        }

        if (!File.Exists(Path.Combine(installDirectory, ManifestFileName)))
        {
            throw new InvalidOperationException("未找到 LittleFish 安装清单，已停止卸载以避免误删文件。");
        }
    }

    private static void CloseInstalledProcesses(string installDirectory)
    {
        var targetPath = Path.GetFullPath(Path.Combine(installDirectory, AppExeName));
        foreach (var process in Process.GetProcessesByName("LittleFish"))
        {
            using (process)
            {
                string? executablePath;
                try
                {
                    executablePath = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(executablePath)
                    || !string.Equals(Path.GetFullPath(executablePath), targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1500))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(2500);
                    }
                }
                catch
                {
                }
            }
        }
    }

    private static void DeleteShortcuts(InstallScope installScope)
    {
        var desktopFolder = Environment.GetFolderPath(installScope == InstallScope.Machine
            ? Environment.SpecialFolder.CommonDesktopDirectory
            : Environment.SpecialFolder.DesktopDirectory);
        var startMenuFolder = Environment.GetFolderPath(installScope == InstallScope.Machine
            ? Environment.SpecialFolder.CommonStartMenu
            : Environment.SpecialFolder.StartMenu);
        TryDeleteFile(Path.Combine(
            desktopFolder,
            $"{AppName}.lnk"));
        TryDeleteDirectory(Path.Combine(
            startMenuFolder,
            "Programs",
            AppName), recursive: true);
    }

    private static void DeleteUninstallRegistryEntry(InstallScope installScope)
    {
        using var baseKey = RegistryKey.OpenBaseKey(
            installScope == InstallScope.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            RegistryView.Registry64);
        baseKey.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
    }

    private static InstallScope GetInstallScope(IReadOnlyList<string> args)
    {
        return string.Equals(GetArgumentValue(args, "--scope"), "machine", StringComparison.OrdinalIgnoreCase)
            ? InstallScope.Machine
            : InstallScope.User;
    }

    private static void DeleteManagedFiles(string installDirectory)
    {
        var manifestPath = Path.Combine(installDirectory, ManifestFileName);
        var root = Path.GetFullPath(installDirectory) + Path.DirectorySeparatorChar;
        var managedFiles = File.ReadAllLines(manifestPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim().Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path.Length)
            .ToArray();

        foreach (var relativePath in managedFiles)
        {
            var fullPath = Path.GetFullPath(Path.Combine(installDirectory, relativePath));
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(fullPath, manifestPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("安装清单中包含无效路径。");
            }

            TryDeleteFile(fullPath);
        }

        TryDeleteFile(manifestPath);
        foreach (var directory in Directory.EnumerateDirectories(installDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            TryDeleteDirectory(directory, recursive: false);
        }
    }

    private static void WaitForParent(int parentId)
    {
        try
        {
            using var parent = Process.GetProcessById(parentId);
            parent.WaitForExit(5000);
        }
        catch
        {
        }
    }

    private static string? GetArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string QuoteArgument(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path, bool recursive)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive);
            }
        }
        catch
        {
        }
    }

    private static void ShowMessage(string message, uint icon)
    {
        MessageBoxW(IntPtr.Zero, message, AppName, MbOk | icon);
    }

    private static void ScheduleTemporaryCopyCleanup()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        MoveFileExW(executablePath, null, MoveFileDelayUntilReboot);
        var directory = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            MoveFileExW(directory, null, MoveFileDelayUntilReboot);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr windowHandle, string text, string caption, uint type);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string existingFileName, string? newFileName, uint flags);
}
