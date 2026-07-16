using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Windows;
using System.Windows.Input;

namespace LittleFish.Installer;

public partial class MainWindow : Window
{
    private const string AppName = "LittleFish";
    private const string ExeName = "LittleFish.exe";
    private const string UninstallerExeName = "LittleFish_Uninstall.exe";
    private const string UninstallTempFlag = "--uninstall-temp";
    private const string PayloadResource = "Assets/payload.zip";
    private const string PayloadHashResource = "Assets/payload.sha256";
    private const string InstallManifestName = ".littlefish-manifest.txt";
    private const string SettingsFileName = "settings.json";
    private enum SetupMode
    {
        Install,
        Uninstall
    }

    private enum InstallAction
    {
        FreshInstall,
        Upgrade,
        Repair,
        DowngradeBlocked
    }

    private enum InstallScope
    {
        User,
        Machine
    }

    private sealed record RegisteredInstallInfo(string InstallPath, string DisplayVersion, InstallScope Scope);

    [Flags]
    private enum MoveFileFlags
    {
        DelayUntilReboot = 0x00000004
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, MoveFileFlags flags);

    private readonly string _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    private readonly SetupMode _mode;
    private readonly bool _isElevatedResume;
    private string? _uninstallPath;
    private string? _registeredInstallPath;
    private string? _registeredVersion;
    private InstallScope? _registeredScope;
    private InstallAction _installAction = InstallAction.FreshInstall;
    private InstallScope _installScope = InstallScope.User;
    private int _pageIndex;
    private bool _isInstalling;
    private bool _installSucceeded;
    private bool _uninstallSucceeded;
    private bool _uninstallFinished;
    private bool HasExistingInstall => !string.IsNullOrWhiteSpace(_registeredInstallPath);
    private bool IsUpdateInstall => _installAction == InstallAction.Upgrade;
    private bool IsRepairInstall => _installAction == InstallAction.Repair;
    private bool IsDowngradeBlocked => _installAction == InstallAction.DowngradeBlocked;
    private string OperationName => _installAction switch
    {
        InstallAction.Upgrade => "更新",
        InstallAction.Repair => "修复",
        _ => "安装"
    };

    public MainWindow()
    {
        _isElevatedResume = HasCommandLineFlag("--elevated-install");
        if (TryGetUninstallPath(out var pendingUninstallPath) && TryRelaunchUninstallerFromTemp(pendingUninstallPath))
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }

        InitializeComponent();
        FooterText.Text = $"LittleFish v{_version}";
        if (!string.IsNullOrWhiteSpace(pendingUninstallPath))
        {
            _mode = SetupMode.Uninstall;
            _uninstallPath = pendingUninstallPath;
            ConfigureUninstallMode();
            return;
        }

        _mode = SetupMode.Install;
        ConfigureInstallMode();
        if (_isElevatedResume)
        {
            Loaded += ResumeElevatedInstallAsync;
        }
    }

    private void ConfigureInstallMode()
    {
        var defaultInstallPath = NormalizeInstallPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs"));

        var registered = ReadRegisteredInstallInfo();
        if (registered is not null)
        {
            _registeredInstallPath = NormalizeExistingInstallPath(registered.InstallPath);
            _registeredVersion = ResolveInstalledVersion(registered);
            _registeredScope = registered.Scope;
            _installScope = ResolveInstallScope(_registeredInstallPath, registered.Scope);
            _installAction = CompareInstallVersions(_registeredVersion, _version);
            InstallPathBox.Text = _registeredInstallPath;
            InstallPathBox.IsReadOnly = true;
            BrowseButton.IsEnabled = false;
            LoadShortcutSelections(_installScope);

            switch (_installAction)
            {
                case InstallAction.Upgrade:
                    WelcomeTitle.Text = "更新 LittleFish";
                    WelcomeSubtitle.Text = $"将 LittleFish 从 {_registeredVersion} 更新到 {_version}。";
                    OptionsMessage.Text = $"将更新现有安装：{_registeredInstallPath}";
                    break;
                case InstallAction.Repair:
                    WelcomeTitle.Text = "修复 LittleFish";
                    WelcomeSubtitle.Text = $"已安装 LittleFish {_version}，可以重新写入程序文件。";
                    OptionsMessage.Text = $"将修复现有安装：{_registeredInstallPath}";
                    break;
                case InstallAction.DowngradeBlocked:
                    WelcomeTitle.Text = "已安装较新版本";
                    WelcomeSubtitle.Text = $"电脑中的 LittleFish {_registeredVersion} 新于此安装包 {_version}，已阻止降级覆盖。";
                    OptionsMessage.Text = "如需安装旧版本，请先卸载当前版本。";
                    break;
            }
        }
        else
        {
            InstallPathBox.Text = defaultInstallPath;
        }

        if (_isElevatedResume)
        {
            var requestedPath = GetCommandLineOption("--install-path");
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                InstallPathBox.Text = HasExistingInstall
                    ? NormalizeExistingInstallPath(requestedPath)
                    : NormalizeInstallPath(requestedPath);
            }

            _installScope = InstallScope.Machine;
            DesktopShortcutBox.IsChecked = GetBooleanCommandLineOption("--desktop-shortcut", defaultValue: true);
            StartMenuShortcutBox.IsChecked = GetBooleanCommandLineOption("--start-menu-shortcut", defaultValue: true);
        }

        UpdatePage();
    }

    private async void ResumeElevatedInstallAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= ResumeElevatedInstallAsync;
        if (!IsProcessElevated())
        {
            OptionsMessage.Text = "未获得管理员权限，无法安装到受保护目录。";
            _pageIndex = 1;
            UpdatePage();
            return;
        }

        if (IsDowngradeBlocked || !ValidateInstallOptions())
        {
            _pageIndex = IsDowngradeBlocked ? 0 : 1;
            UpdatePage();
            return;
        }

        _pageIndex = 2;
        UpdatePage();
        await InstallAsync();
    }

    private void ConfigureUninstallMode()
    {
        Width = 500;
        Height = 250;
        MinWidth = 500;
        MinHeight = 250;
        OuterFrame.Margin = new Thickness(16);
        OuterFrame.CornerRadius = new CornerRadius(22);
        TitleRow.Height = new GridLength(50);
        FooterRow.Height = new GridLength(64);
        TitleBar.CornerRadius = new CornerRadius(22, 22, 0, 0);
        FooterBar.CornerRadius = new CornerRadius(0, 0, 22, 22);
        MainContentGrid.Margin = new Thickness(24, 18, 24, 12);
        StepColumn.Width = new GridLength(0);
        StepRail.Visibility = Visibility.Collapsed;
        BodyPanel.Margin = new Thickness(0);
        FooterText.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Collapsed;
        Title = "LittleFish 卸载";
        WindowTitleText.Text = "卸载 LittleFish";
        WelcomeTitle.Text = "卸载 LittleFish";
        WelcomeSubtitle.Text = "将从此电脑移除 LittleFish 和相关快捷方式。";
        WelcomeHeroCard.Visibility = Visibility.Collapsed;
        UninstallSummaryCard.Visibility = Visibility.Collapsed;
        _pageIndex = 0;
        UpdatePage();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling)
        {
            return;
        }

        Close();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_pageIndex > 0 && !_isInstalling)
        {
            _pageIndex--;
            UpdatePage();
        }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling)
        {
            return;
        }

        if (_mode == SetupMode.Uninstall)
        {
            await HandleUninstallNextAsync();
            return;
        }

        if (_installSucceeded)
        {
            LaunchInstalledAppIfNeeded();
            Close();
            return;
        }

        if (_pageIndex == 0)
        {
            if (IsDowngradeBlocked)
            {
                Close();
                return;
            }

            _pageIndex = 1;
            UpdatePage();
            return;
        }

        if (_pageIndex == 1)
        {
            if (!ValidateInstallOptions())
            {
                return;
            }

            if (_installScope == InstallScope.Machine && !IsProcessElevated())
            {
                if (TryRelaunchElevatedInstaller())
                {
                    System.Windows.Application.Current.Shutdown();
                }

                return;
            }

            _pageIndex = 2;
            UpdatePage();
            await InstallAsync();
        }
    }

    private async Task HandleUninstallNextAsync()
    {
        if (_uninstallFinished)
        {
            Close();
            return;
        }

        UpdatePage();
        await UninstallAsync();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (HasExistingInstall)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择安装位置，LittleFish 会安装到所选位置下的 LittleFish 文件夹中",
            InitialDirectory = GetInstallParentPath(InstallPathBox.Text),
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            InstallPathBox.Text = NormalizeInstallPath(dialog.FolderName);
        }
    }

    private bool ValidateInstallOptions()
    {
        var installPath = InstallPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installPath))
        {
            OptionsMessage.Text = "请选择安装目录。";
            return false;
        }

        try
        {
            var fullPath = HasExistingInstall && !string.IsNullOrWhiteSpace(_registeredInstallPath)
                ? NormalizeExistingInstallPath(_registeredInstallPath)
                : NormalizeInstallPath(installPath);

            InstallPathBox.Text = fullPath;
            _installScope = ResolveInstallScope(fullPath, HasExistingInstall ? _installScope : InstallScope.User);
            OptionsMessage.Text = HasExistingInstall
                ? $"将{OperationName}现有安装：{fullPath}"
                : _installScope == InstallScope.Machine
                    ? "此位置需要管理员权限，开始安装时会请求 UAC 授权。"
                    : "";
            return true;
        }
        catch (Exception ex)
        {
            OptionsMessage.Text = $"安装目录无效：{ex.Message}";
            return false;
        }
    }

    private async Task InstallAsync()
    {
        _isInstalling = true;
        SetProgress(4, $"正在准备{OperationName}...");

        try
        {
            await Task.Run(() => InstallCore());
            _installSucceeded = true;
            SetProgress(100, $"{OperationName}完成。");
            InstallTitle.Text = $"{OperationName}完成";
            InstallSubtitle.Text = _installAction == InstallAction.Upgrade
                ? "LittleFish 已经更新好了。"
                : _installAction == InstallAction.Repair
                    ? "LittleFish 已经修复好了。"
                    : "LittleFish 已经准备好了。";
            InstallResultCard.Visibility = Visibility.Visible;
            InstallResultText.Text = $"已{OperationName}：{InstallPathBox.Text.Trim()}";
            LaunchAfterInstallBox.Visibility = Visibility.Visible;
            NextButton.Content = "完成";
            NextButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
            BackButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            InstallTitle.Text = $"{OperationName}失败";
            InstallSubtitle.Text = $"{OperationName}过程中遇到问题，已尝试恢复原有文件。";
            InstallResultCard.Visibility = Visibility.Visible;
            InstallResultText.Text = ex.Message;
            LaunchAfterInstallBox.Visibility = Visibility.Collapsed;
            ProgressText.Text = $"{OperationName}失败。";
            NextButton.Content = "重试";
            NextButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Visible;
            _pageIndex = 1;
        }
        finally
        {
            _isInstalling = false;
            UpdateStepState();
        }
    }

    private void InstallCore()
    {
        var installPath = InstallPathBox.Dispatcher.Invoke(() => InstallPathBox.Text.Trim());
        var createDesktopShortcut = DesktopShortcutBox.Dispatcher.Invoke(() => DesktopShortcutBox.IsChecked == true);
        var createStartMenuShortcut = StartMenuShortcutBox.Dispatcher.Invoke(() => StartMenuShortcutBox.IsChecked == true);
        var desktopShortcutPath = GetDesktopShortcutPath(_installScope);
        var startMenuDirectory = GetStartMenuDirectory(_installScope);
        var hadDesktopShortcut = File.Exists(desktopShortcutPath);
        var hadStartMenuShortcut = Directory.Exists(startMenuDirectory);

        DispatchProgress(8, HasExistingInstall ? "正在关闭旧版本..." : "正在检查运行状态...");
        CloseRunningLittleFishProcesses(installPath, waitMilliseconds: 1800);

        DispatchProgress(12, "正在准备安装事务...");
        var payloadPath = ExtractPayloadToTempFile();
        var transactionRoot = CreateTransactionDirectory(installPath);
        var stagingPath = Path.Combine(transactionRoot, "staging");
        var rollbackPath = Path.Combine(transactionRoot, "rollback");
        var managedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var transactionStarted = false;
        var stagedFilesStarted = false;
        try
        {
            Directory.CreateDirectory(stagingPath);
            Directory.CreateDirectory(rollbackPath);

            DispatchProgress(24, "正在校验并解压程序文件...");
            managedFiles = ExtractPayloadToInstallDirectory(payloadPath, stagingPath);
            managedFiles.Add(NormalizeRelativePath(UninstallerExeName));
            managedFiles.Add(NormalizeRelativePath(InstallManifestName));

            if (!File.Exists(Path.Combine(stagingPath, ExeName)))
            {
                throw new FileNotFoundException("安装包中缺少 LittleFish 主程序。", Path.Combine(stagingPath, ExeName));
            }
            if (!File.Exists(Path.Combine(stagingPath, UninstallerExeName)))
            {
                throw new FileNotFoundException("安装包中缺少轻量卸载程序。", Path.Combine(stagingPath, UninstallerExeName));
            }

            DispatchProgress(48, "正在备份现有版本...");
            var previousManagedFiles = GetPreviousManagedFiles(installPath);
            transactionStarted = true;
            BackupManagedFiles(installPath, rollbackPath, previousManagedFiles.Concat(managedFiles));

            DispatchProgress(62, $"正在{OperationName}程序文件...");
            stagedFilesStarted = true;
            MoveStagedFiles(stagingPath, installPath);
            WriteInstallManifest(installPath, managedFiles);

            var exePath = Path.Combine(installPath, ExeName);
            var uninstallerPath = CreateInstalledUninstaller(installPath);

            DispatchProgress(78, "正在同步快捷方式...");
            SyncShortcuts(
                _installScope,
                createDesktopShortcut,
                createStartMenuShortcut,
                exePath,
                uninstallerPath,
                installPath);

            DispatchProgress(90, "正在写入卸载信息...");
            RegisterUninstaller(installPath, exePath, uninstallerPath);
            RemoveObsoleteRegistrationAfterScopeMigration();

            DispatchProgress(96, $"正在完成{OperationName}...");
        }
        catch
        {
            if (transactionStarted)
            {
                RollbackManagedFiles(
                    installPath,
                    rollbackPath,
                    stagedFilesStarted ? managedFiles : Array.Empty<string>());
                TryRestoreShortcutState(
                    _installScope,
                    hadDesktopShortcut,
                    hadStartMenuShortcut,
                    installPath);
                RestorePreviousRegistration();
            }

            throw;
        }
        finally
        {
            TryDelete(payloadPath);
            TryDeleteDirectory(transactionRoot);
        }
    }

    private async Task UninstallAsync()
    {
        _isInstalling = true;
        NextButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        WelcomeTitle.Text = "正在卸载 LittleFish";
        WelcomeSubtitle.Text = "正在移除程序文件和快捷方式。";
        UninstallSummaryCard.Visibility = Visibility.Collapsed;

        try
        {
            await Task.Run(() => UninstallCore());
            _uninstallFinished = true;
            _uninstallSucceeded = true;
            WelcomeTitle.Text = "卸载完成";
            WelcomeSubtitle.Text = "LittleFish 已从此电脑移除。";
            NextButton.Content = "完成";
            NextButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _uninstallFinished = true;
            WelcomeTitle.Text = "卸载失败";
            WelcomeSubtitle.Text = "卸载过程中遇到问题。";
            UninstallSummaryCard.Visibility = Visibility.Visible;
            UninstallPathText.Text = ex.Message;
            NextButton.Content = "关闭";
            NextButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _isInstalling = false;
        }
    }

    private void UninstallCore()
    {
        var installPath = _uninstallPath;
        if (string.IsNullOrWhiteSpace(installPath))
        {
            throw new InvalidOperationException("未找到 LittleFish 的安装目录。");
        }
        installPath = Path.GetFullPath(installPath);
        if (IsDriveRoot(installPath))
        {
            throw new InvalidOperationException("卸载目录不能是磁盘根目录。");
        }

        DispatchProgress(18, "正在关闭 LittleFish...");
        CloseRunningLittleFishProcesses(installPath, waitMilliseconds: 1500);

        DispatchProgress(36, "正在删除快捷方式...");
        TryDeleteFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            $"{AppName}.lnk"));
        TryDeleteDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            AppName));

        DispatchProgress(54, "正在删除卸载信息...");
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}", throwOnMissingSubKey: false);

        if (Directory.Exists(installPath))
        {
            var appExe = Path.Combine(installPath, ExeName);
            var uninstallerExe = Path.Combine(installPath, UninstallerExeName);
            if (!File.Exists(appExe) && !File.Exists(uninstallerExe))
            {
                throw new InvalidOperationException("目标目录不像 LittleFish 的安装目录，已停止卸载以避免误删文件。");
            }

            DispatchProgress(72, "正在删除程序文件...");
            Directory.Delete(installPath, recursive: true);
        }

        ScheduleCurrentTempExecutableCleanup();

        DispatchProgress(92, "正在完成卸载...");
    }

    private string ExtractPayloadToTempFile()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri(PayloadResource, UriKind.Relative));
        if (info is null)
        {
            throw new InvalidOperationException("安装包缺少程序文件 payload。请重新构建安装包。");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "LittleFishSetup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var payloadPath = Path.Combine(tempDir, "payload.zip");
        using (info.Stream)
        using (var output = File.Create(payloadPath))
        {
            info.Stream.CopyTo(output);
        }

        var hashInfo = System.Windows.Application.GetResourceStream(new Uri(PayloadHashResource, UriKind.Relative));
        if (hashInfo is null)
        {
            TryDelete(payloadPath);
            throw new InvalidOperationException("安装包缺少 payload 完整性信息。请重新构建安装包。");
        }

        string expectedHash;
        using (hashInfo.Stream)
        using (var reader = new StreamReader(hashInfo.Stream))
        {
            expectedHash = reader.ReadToEnd().Trim();
        }

        string actualHash;
        using (var payloadStream = File.OpenRead(payloadPath))
        {
            actualHash = Convert.ToHexString(SHA256.HashData(payloadStream));
        }
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(payloadPath);
            throw new InvalidDataException("安装包中的程序文件校验失败，安装已停止。");
        }

        return payloadPath;
    }

    private static string CreateTransactionDirectory(string installPath)
    {
        var installParent = Path.GetDirectoryName(Path.GetFullPath(installPath))
            ?? throw new InvalidOperationException("无法确定安装目录的上级目录。");
        Directory.CreateDirectory(installParent);

        var transactionRoot = Path.Combine(
            installParent,
            $".LittleFish.setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(transactionRoot);
        return transactionRoot;
    }

    private static HashSet<string> GetPreviousManagedFiles(string installPath)
    {
        var previousManifest = ReadInstallManifest(installPath);
        if (previousManifest.Count > 0 || !Directory.Exists(installPath))
        {
            return previousManifest;
        }

        foreach (var filePath in Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = GetRelativeInstallPath(installPath, filePath);
            if (IsLegacyManagedFile(relativePath))
            {
                previousManifest.Add(relativePath);
            }
        }

        return previousManifest;
    }

    private static void BackupManagedFiles(
        string installPath,
        string rollbackPath,
        IEnumerable<string> managedFiles)
    {
        if (!Directory.Exists(installPath))
        {
            return;
        }

        foreach (var relativePath in managedFiles
                     .Select(NormalizeRelativePath)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(relativePath), SettingsFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourcePath = ResolveInstallPath(installPath, relativePath);
            if (!File.Exists(sourcePath) || IsCurrentProcessFile(sourcePath))
            {
                continue;
            }

            var backupPath = ResolveInstallPath(rollbackPath, relativePath);
            var backupDirectory = Path.GetDirectoryName(backupPath);
            if (!string.IsNullOrWhiteSpace(backupDirectory))
            {
                Directory.CreateDirectory(backupDirectory);
            }

            try
            {
                File.Move(sourcePath, backupPath, overwrite: true);
            }
            catch (IOException exception)
            {
                throw CreateFileInUseException(sourcePath, exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw CreateFileInUseException(sourcePath, exception);
            }
        }
    }

    private static void MoveStagedFiles(string stagingPath, string installPath)
    {
        Directory.CreateDirectory(installPath);
        foreach (var sourcePath in Directory.EnumerateFiles(stagingPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(stagingPath, sourcePath));
            var destinationPath = ResolveInstallPath(installPath, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Move(sourcePath, destinationPath, overwrite: false);
        }
    }

    private static void RollbackManagedFiles(
        string installPath,
        string rollbackPath,
        IEnumerable<string> newManagedFiles)
    {
        foreach (var relativePath in newManagedFiles
                     .Select(NormalizeRelativePath)
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFileName(relativePath), SettingsFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            TryDeleteFile(ResolveInstallPath(installPath, relativePath));
        }

        if (!Directory.Exists(rollbackPath))
        {
            return;
        }

        foreach (var backupPath in Directory.EnumerateFiles(rollbackPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = NormalizeRelativePath(Path.GetRelativePath(rollbackPath, backupPath));
            var destinationPath = ResolveInstallPath(installPath, relativePath);
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            File.Move(backupPath, destinationPath, overwrite: true);
        }

        DeleteEmptyInstallDirectories(installPath);
    }

    private static HashSet<string> ExtractPayloadToInstallDirectory(string payloadPath, string installPath)
    {
        var installedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var archive = ZipFile.OpenRead(payloadPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }

            var relativePath = NormalizeRelativePath(entry.FullName);
            installedFiles.Add(relativePath);
            var destinationPath = ResolveInstallPath(installPath, relativePath);

            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (FileMatchesZipEntry(destinationPath, entry))
            {
                continue;
            }

            try
            {
                using var source = entry.Open();
                using var target = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
                source.CopyTo(target);
            }
            catch (IOException ex)
            {
                throw CreateFileInUseException(destinationPath, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw CreateFileInUseException(destinationPath, ex);
            }
        }

        return installedFiles;
    }

    private static void CleanupObsoleteInstalledFiles(string installPath, ISet<string> currentManagedFiles)
    {
        var previousManifest = ReadInstallManifest(installPath);
        if (previousManifest.Count > 0)
        {
            foreach (var relativePath in previousManifest)
            {
                if (!currentManagedFiles.Contains(relativePath))
                {
                    DeleteInstalledFileIfExists(installPath, relativePath);
                }
            }
        }
        else
        {
            CleanupLegacyManagedFiles(installPath, currentManagedFiles);
        }

        DeleteEmptyInstallDirectories(installPath);
    }

    private static HashSet<string> ReadInstallManifest(string installPath)
    {
        var manifestPath = Path.Combine(installPath, InstallManifestName);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(manifestPath))
        {
            return files;
        }

        foreach (var line in File.ReadAllLines(manifestPath))
        {
            var relativePath = NormalizeRelativePath(line);
            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                files.Add(relativePath);
            }
        }

        return files;
    }

    private static void WriteInstallManifest(string installPath, IEnumerable<string> installedFiles)
    {
        var manifestPath = Path.Combine(installPath, InstallManifestName);
        var lines = installedFiles
            .Select(NormalizeRelativePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        File.WriteAllLines(manifestPath, lines);
    }

    private static void CleanupLegacyManagedFiles(string installPath, ISet<string> currentManagedFiles)
    {
        if (!Directory.Exists(installPath))
        {
            return;
        }

        foreach (var filePath in Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = GetRelativeInstallPath(installPath, filePath);
            if (currentManagedFiles.Contains(relativePath) || !IsLegacyManagedFile(relativePath))
            {
                continue;
            }

            DeleteInstalledFileIfExists(installPath, relativePath);
        }
    }

    private static bool IsLegacyManagedFile(string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        if (string.Equals(fileName, SettingsFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, InstallManifestName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(relativePath);
        if (string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".config", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteInstalledFileIfExists(string installPath, string relativePath)
    {
        var fileName = Path.GetFileName(relativePath);
        if (string.Equals(fileName, SettingsFileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var filePath = ResolveInstallPath(installPath, relativePath);
        if (!File.Exists(filePath) || IsCurrentProcessFile(filePath))
        {
            return;
        }

        try
        {
            File.Delete(filePath);
        }
        catch (IOException ex)
        {
            throw CreateFileInUseException(filePath, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw CreateFileInUseException(filePath, ex);
        }
    }

    private static bool IsCurrentProcessFile(string filePath)
    {
        var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        return !string.IsNullOrWhiteSpace(currentExe)
            && string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteEmptyInstallDirectories(string installPath)
    {
        if (!Directory.Exists(installPath))
        {
            return;
        }

        foreach (var directory in Directory
            .EnumerateDirectories(installPath, "*", SearchOption.AllDirectories)
            .OrderByDescending(path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string ResolveInstallPath(string installPath, string relativePath)
    {
        var installRoot = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var destinationPath = Path.GetFullPath(Path.Combine(installPath, NormalizeRelativePath(relativePath)
            .Replace('/', Path.DirectorySeparatorChar)));

        if (!destinationPath.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装包中包含无效路径，已停止安装。");
        }

        return destinationPath;
    }

    private static string GetRelativeInstallPath(string installPath, string filePath)
    {
        var installRoot = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(filePath);
        if (!fullPath.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装目录中包含无效路径，已停止清理。");
        }

        return NormalizeRelativePath(fullPath[installRoot.Length..]);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath
            .Replace('\\', '/')
            .TrimStart('/');
    }

    private static bool FileMatchesZipEntry(string destinationPath, ZipArchiveEntry entry)
    {
        if (!File.Exists(destinationPath))
        {
            return false;
        }

        var fileInfo = new FileInfo(destinationPath);
        if (fileInfo.Length != entry.Length)
        {
            return false;
        }

        try
        {
            using var existing = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var incoming = entry.Open();
            return StreamsEqual(existing, incoming);
        }
        catch (IOException ex)
        {
            throw CreateFileInUseException(destinationPath, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw CreateFileInUseException(destinationPath, ex);
        }
    }

    private static bool StreamsEqual(Stream left, Stream right)
    {
        var leftBuffer = new byte[81920];
        var rightBuffer = new byte[81920];

        while (true)
        {
            var leftRead = left.Read(leftBuffer, 0, leftBuffer.Length);
            var rightRead = right.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            for (var i = 0; i < leftRead; i++)
            {
                if (leftBuffer[i] != rightBuffer[i])
                {
                    return false;
                }
            }
        }
    }

    private static IOException CreateFileInUseException(string path, Exception innerException)
    {
        return new IOException(
            $"无法更新文件：{Path.GetFileName(path)}。请确认 LittleFish 已完全退出；如果仍然失败，请重启电脑后再运行安装包。\n\n文件位置：{path}",
            innerException);
    }

    private static void CloseRunningLittleFishProcesses(string installPath, int waitMilliseconds)
    {
        foreach (var process in Process.GetProcessesByName("LittleFish"))
        {
            try
            {
                if (!IsLittleFishProcessInInstallPath(process, installPath))
                {
                    continue;
                }

                process.CloseMainWindow();
                if (!process.WaitForExit(waitMilliseconds))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(waitMilliseconds);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static bool IsLittleFishProcessInInstallPath(Process process, string installPath)
    {
        try
        {
            var expectedPath = Path.GetFullPath(Path.Combine(installPath, ExeName));
            var processPath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(processPath) &&
                string.Equals(Path.GetFullPath(processPath), expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeInstallPath(string installPath)
    {
        var fullPath = Path.GetFullPath(installPath.Trim());
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return fullPath;
        }

        if (IsDriveRoot(fullPath))
        {
            return Path.Combine(root, AppName);
        }

        var trimmedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(trimmedPath);
        return string.Equals(folderName, AppName, StringComparison.OrdinalIgnoreCase)
            ? trimmedPath
            : Path.Combine(trimmedPath, AppName);
    }

    private static string NormalizeExistingInstallPath(string installPath)
    {
        var fullPath = Path.GetFullPath(installPath.Trim());
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return fullPath;
        }

        return IsDriveRoot(fullPath)
            ? Path.Combine(root, AppName)
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string GetInstallParentPath(string installPath)
    {
        var fullPath = Path.GetFullPath(installPath.Trim());
        var trimmedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folderName = Path.GetFileName(trimmedPath);
        if (string.Equals(folderName, AppName, StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(trimmedPath) ?? trimmedPath;
        }

        return trimmedPath;
    }

    private static bool IsDriveRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var trimmedFullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(trimmedFullPath, trimmedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static InstallScope ResolveInstallScope(string installPath, InstallScope preferredScope)
    {
        if (preferredScope == InstallScope.Machine)
        {
            return InstallScope.Machine;
        }

        var protectedRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("WINDIR") ?? string.Empty
        };

        return protectedRoots.Any(root => IsPathWithinRoot(installPath, root))
            ? InstallScope.Machine
            : InstallScope.User;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static InstallAction CompareInstallVersions(string installedVersion, string packageVersion)
    {
        if (!TryParseVersion(packageVersion, out var package))
        {
            throw new InvalidOperationException($"安装包版本号无效：{packageVersion}");
        }

        if (!TryParseVersion(installedVersion, out var installed))
        {
            return InstallAction.Repair;
        }

        var comparison = installed.CompareTo(package);
        return comparison < 0
            ? InstallAction.Upgrade
            : comparison == 0
                ? InstallAction.Repair
                : InstallAction.DowngradeBlocked;
    }

    private static bool TryParseVersion(string? value, out Version version)
    {
        var normalized = (value ?? string.Empty).Trim().TrimStart('v', 'V');
        var metadataIndex = normalized.IndexOfAny(['-', '+']);
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        return Version.TryParse(normalized, out version!);
    }

    private static string ResolveInstalledVersion(RegisteredInstallInfo registered)
    {
        if (TryParseVersion(registered.DisplayVersion, out _))
        {
            return registered.DisplayVersion;
        }

        var executablePath = Path.Combine(registered.InstallPath, ExeName);
        if (File.Exists(executablePath))
        {
            var productVersion = FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
            if (TryParseVersion(productVersion, out _))
            {
                return productVersion!;
            }
        }

        return "未知版本";
    }

    private void LoadShortcutSelections(InstallScope scope)
    {
        DesktopShortcutBox.IsChecked = File.Exists(GetDesktopShortcutPath(scope));
        StartMenuShortcutBox.IsChecked = Directory.Exists(GetStartMenuDirectory(scope));
    }

    private static string GetDesktopShortcutPath(InstallScope scope)
    {
        var desktopFolder = Environment.GetFolderPath(scope == InstallScope.Machine
            ? Environment.SpecialFolder.CommonDesktopDirectory
            : Environment.SpecialFolder.DesktopDirectory);
        return Path.Combine(desktopFolder, $"{AppName}.lnk");
    }

    private static string GetStartMenuDirectory(InstallScope scope)
    {
        var startMenuFolder = Environment.GetFolderPath(scope == InstallScope.Machine
            ? Environment.SpecialFolder.CommonStartMenu
            : Environment.SpecialFolder.StartMenu);
        return Path.Combine(startMenuFolder, "Programs", AppName);
    }

    private static void SyncShortcuts(
        InstallScope scope,
        bool createDesktopShortcut,
        bool createStartMenuShortcut,
        string exePath,
        string uninstallerPath,
        string installPath)
    {
        var desktopShortcut = GetDesktopShortcutPath(scope);
        if (createDesktopShortcut)
        {
            CreateShortcut(desktopShortcut, exePath, installPath, AppName);
        }
        else
        {
            TryDeleteFile(desktopShortcut);
        }

        var startMenuDirectory = GetStartMenuDirectory(scope);
        if (!createStartMenuShortcut)
        {
            TryDeleteDirectory(startMenuDirectory);
            return;
        }

        Directory.CreateDirectory(startMenuDirectory);
        CreateShortcut(Path.Combine(startMenuDirectory, $"{AppName}.lnk"), exePath, installPath, AppName);
        CreateShortcut(
            Path.Combine(startMenuDirectory, $"卸载 {AppName}.lnk"),
            uninstallerPath,
            installPath,
            $"卸载 {AppName}",
            $"--uninstall \"{installPath}\" --scope {GetScopeArgument(scope)}");
    }

    private static void TryRestoreShortcutState(
        InstallScope scope,
        bool hadDesktopShortcut,
        bool hadStartMenuShortcut,
        string installPath)
    {
        try
        {
            var exePath = Path.Combine(installPath, ExeName);
            var uninstallerPath = Path.Combine(installPath, UninstallerExeName);
            SyncShortcuts(
                scope,
                hadDesktopShortcut,
                hadStartMenuShortcut,
                exePath,
                uninstallerPath,
                installPath);
        }
        catch
        {
        }
    }

    private bool TryRelaunchElevatedInstaller()
    {
        var currentExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法定位当前安装程序。");
        var startInfo = new ProcessStartInfo
        {
            FileName = currentExecutable,
            WorkingDirectory = Path.GetDirectoryName(currentExecutable),
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add("--elevated-install");
        startInfo.ArgumentList.Add("--install-path");
        startInfo.ArgumentList.Add(InstallPathBox.Text.Trim());
        startInfo.ArgumentList.Add("--desktop-shortcut");
        startInfo.ArgumentList.Add(DesktopShortcutBox.IsChecked == true ? "1" : "0");
        startInfo.ArgumentList.Add("--start-menu-shortcut");
        startInfo.ArgumentList.Add(StartMenuShortcutBox.IsChecked == true ? "1" : "0");

        try
        {
            Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            OptionsMessage.Text = "已取消管理员授权，尚未写入任何安装文件。";
            return false;
        }
    }

    private static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool HasCommandLineFlag(string name)
    {
        return Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetCommandLineOption(string name)
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 1; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }

    private static bool GetBooleanCommandLineOption(string name, bool defaultValue)
    {
        return GetCommandLineOption(name) switch
        {
            "1" => true,
            "0" => false,
            _ => defaultValue
        };
    }

    private static string GetScopeArgument(InstallScope scope) =>
        scope == InstallScope.Machine ? "machine" : "user";

    internal static void RunPackageSelfTests()
    {
        if (CompareInstallVersions("1.5.1", "1.5.2") != InstallAction.Upgrade
            || CompareInstallVersions("1.5.2", "1.5.2") != InstallAction.Repair
            || CompareInstallVersions("1.5.3", "1.5.2") != InstallAction.DowngradeBlocked
            || CompareInstallVersions("未知版本", "1.5.2") != InstallAction.Repair)
        {
            throw new InvalidOperationException("安装版本比较自检失败。");
        }

        var programFilesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            AppName);
        if (ResolveInstallScope(programFilesPath, InstallScope.User) != InstallScope.Machine)
        {
            throw new InvalidOperationException("受保护目录识别自检失败。");
        }

        var userPath = Path.Combine(Path.GetTempPath(), "LittleFish.PackageSmoke", AppName);
        if (ResolveInstallScope(userPath, InstallScope.User) != InstallScope.User)
        {
            throw new InvalidOperationException("当前用户安装范围自检失败。");
        }
    }

    private static bool TryGetUninstallPath(out string uninstallPath)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--uninstall", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(args[i], "/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                uninstallPath = Path.GetFullPath(args[i + 1]);
                return true;
            }

            uninstallPath = ReadRegisteredInstallPath()
                ?? Path.GetDirectoryName(Environment.ProcessPath ?? "") 
                ?? "";
            return true;
        }

        uninstallPath = "";
        return false;
    }

    private static bool TryRelaunchUninstallerFromTemp(string installPath)
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Any(arg => string.Equals(arg, UninstallTempFlag, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
        {
            return false;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "LittleFishUninstall", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempExe = Path.Combine(tempDir, UninstallerExeName);
        File.Copy(currentExe, tempExe, overwrite: true);

        Process.Start(new ProcessStartInfo
        {
            FileName = tempExe,
            Arguments = $"--uninstall \"{installPath}\" {UninstallTempFlag}",
            UseShellExecute = true
        });
        return true;
    }

    private static string? ReadRegisteredInstallPath()
    {
        return ReadRegisteredInstallInfo()?.InstallPath;
    }

    private static RegisteredInstallInfo? ReadRegisteredInstallInfo()
    {
        return ReadRegisteredInstallInfo(InstallScope.User)
            ?? ReadRegisteredInstallInfo(InstallScope.Machine);
    }

    private static RegisteredInstallInfo? ReadRegisteredInstallInfo(InstallScope scope)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                scope == InstallScope.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                RegistryView.Registry64);
            using var key = baseKey.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}");
            var installPath = key?.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(installPath))
            {
                return null;
            }

            var displayVersion = key?.GetValue("DisplayVersion") as string ?? "未知版本";
            return new RegisteredInstallInfo(installPath, displayVersion, scope);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string CreateInstalledUninstaller(string installPath)
    {
        var uninstallerPath = Path.Combine(installPath, UninstallerExeName);
        if (!File.Exists(uninstallerPath))
        {
            throw new FileNotFoundException("安装包缺少轻量卸载程序。请重新构建安装包。", uninstallerPath);
        }

        return uninstallerPath;
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void ScheduleCurrentTempExecutableCleanup()
    {
        var currentExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(currentExe))
        {
            return;
        }

        var currentDir = Path.GetDirectoryName(currentExe);
        if (!string.IsNullOrWhiteSpace(currentDir)
            && currentDir.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            MoveFileEx(currentExe, null, MoveFileFlags.DelayUntilReboot);
            MoveFileEx(currentDir, null, MoveFileFlags.DelayUntilReboot);
        }
    }

    private void RegisterUninstaller(string installPath, string exePath, string uninstallerPath)
    {
        RegisterUninstaller(_installScope, installPath, exePath, uninstallerPath, _version);
    }

    private static void RegisterUninstaller(
        InstallScope scope,
        string installPath,
        string exePath,
        string uninstallerPath,
        string version)
    {
        using var baseKey = RegistryKey.OpenBaseKey(
            scope == InstallScope.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            RegistryView.Registry64);
        using var key = baseKey.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}", writable: true);
        key?.SetValue("DisplayName", AppName);
        key?.SetValue("DisplayVersion", version);
        key?.SetValue("Publisher", AppName);
        key?.SetValue("InstallLocation", installPath);
        key?.SetValue("DisplayIcon", exePath);
        var uninstallArguments = $"--uninstall \"{installPath}\" --scope {GetScopeArgument(scope)}";
        key?.SetValue("UninstallString", $"\"{uninstallerPath}\" {uninstallArguments}");
        key?.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" {uninstallArguments} --quiet");
        key?.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        var estimatedSize = Directory.Exists(installPath)
            ? Directory.EnumerateFiles(installPath, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length) / 1024L
            : 0L;
        key?.SetValue("EstimatedSize", (int)Math.Min(estimatedSize, int.MaxValue), RegistryValueKind.DWord);
        if (TryParseVersion(version, out var parsedVersion))
        {
            key?.SetValue("VersionMajor", parsedVersion.Major, RegistryValueKind.DWord);
            key?.SetValue("VersionMinor", parsedVersion.Minor, RegistryValueKind.DWord);
        }
        key?.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key?.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private void RemoveObsoleteRegistrationAfterScopeMigration()
    {
        if (_registeredScope is not null && _registeredScope != _installScope)
        {
            DeleteUninstallRegistration(_registeredScope.Value);
        }
    }

    private void RestorePreviousRegistration()
    {
        try
        {
            if (_registeredScope is null
                || string.IsNullOrWhiteSpace(_registeredInstallPath)
                || string.IsNullOrWhiteSpace(_registeredVersion))
            {
                DeleteUninstallRegistration(_installScope);
                return;
            }

            if (_registeredScope != _installScope)
            {
                DeleteUninstallRegistration(_installScope);
            }

            var oldExePath = Path.Combine(_registeredInstallPath, ExeName);
            var oldUninstallerPath = Path.Combine(_registeredInstallPath, UninstallerExeName);
            RegisterUninstaller(
                _registeredScope.Value,
                _registeredInstallPath,
                oldExePath,
                oldUninstallerPath,
                _registeredVersion);
        }
        catch
        {
        }
    }

    private static void DeleteUninstallRegistration(InstallScope scope)
    {
        using var baseKey = RegistryKey.OpenBaseKey(
            scope == InstallScope.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            RegistryView.Registry64);
        baseKey.DeleteSubKeyTree(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppName}",
            throwOnMissingSubKey: false);
    }

    private static void CreateShortcut(
        string shortcutPath,
        string targetPath,
        string workingDirectory,
        string description,
        string arguments = "")
    {
        var shortcutDirectory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrWhiteSpace(shortcutDirectory))
        {
            Directory.CreateDirectory(shortcutDirectory);
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法创建快捷方式：WScript.Shell 不可用。");
        dynamic shell = Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法创建快捷方式。");
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = targetPath;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.Description = description;
        shortcut.Arguments = arguments;
        shortcut.IconLocation = $"{targetPath},0";
        shortcut.Save();
    }

    private void LaunchInstalledAppIfNeeded()
    {
        if (LaunchAfterInstallBox.IsChecked != true)
        {
            return;
        }

        var exePath = Path.Combine(InstallPathBox.Text.Trim(), ExeName);
        if (File.Exists(exePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath),
                UseShellExecute = true
            });
        }
    }

    private void UpdatePage()
    {
        if (_mode == SetupMode.Uninstall)
        {
            UpdateUninstallPage();
            return;
        }

        WelcomePage.Visibility = _pageIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        OptionsPage.Visibility = _pageIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        InstallPage.Visibility = _pageIndex == 2 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _pageIndex > 0 && _pageIndex < 2;
        NextButton.Content = _pageIndex switch
        {
            0 => IsDowngradeBlocked ? "关闭" : "下一步",
            1 => $"开始{OperationName}",
            _ => _installSucceeded ? "完成" : $"{OperationName}中"
        };
        NextButton.IsEnabled = _pageIndex < 2 || _installSucceeded;
        CancelButton.Visibility = _installSucceeded ? Visibility.Collapsed : Visibility.Visible;
        UpdateStepState();
    }

    private void UpdateUninstallPage()
    {
        WelcomePage.Visibility = Visibility.Visible;
        OptionsPage.Visibility = Visibility.Collapsed;
        InstallPage.Visibility = Visibility.Collapsed;

        BackButton.IsEnabled = false;
        NextButton.Content = _uninstallFinished
            ? _uninstallSucceeded ? "完成" : "关闭"
            : "卸载";
        NextButton.IsEnabled = !_isInstalling || _uninstallFinished;
        CancelButton.Visibility = _uninstallFinished ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateStepState()
    {

        StepWelcome.Foreground = _pageIndex == 0 ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.SlateGray;
        StepOptions.Foreground = _pageIndex == 1 ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.SlateGray;
        StepInstall.Foreground = _pageIndex == 2 ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.SlateGray;
        StepWelcome.FontWeight = _pageIndex == 0 ? FontWeights.SemiBold : FontWeights.Normal;
        StepOptions.FontWeight = _pageIndex == 1 ? FontWeights.SemiBold : FontWeights.Normal;
        StepInstall.FontWeight = _pageIndex == 2 ? FontWeights.SemiBold : FontWeights.Normal;
    }

    private void DispatchProgress(double value, string message)
    {
        Dispatcher.Invoke(() => SetProgress(value, message));
    }

    private void SetProgress(double value, string message)
    {
        InstallProgress.Value = value;
        ProgressText.Text = message;
    }

    private static void TryDelete(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
