# LittleFish 独立安装逻辑

本文档只描述 `E:\txt\LittleFish` 独立项目现有的安装、更新、卸载和打包逻辑，不涉及 SeaS 如何内置 LittleFish。

## 1. 安装体系组成

LittleFish 目前有三个工程：

- `src/LittleFish.App/LittleFish.App.csproj`：LittleFish 主程序，当前版本 `1.6.0`。
- `src/LittleFish.Installer/LittleFish.Installer.csproj`：自定义 WPF 安装器，负责安装和覆盖更新界面。
- `src/LittleFish.Uninstaller/LittleFish.Uninstaller.csproj`：轻量卸载器，不携带安装 payload，不使用 WPF。

配套脚本和输出：

- `scripts/build-installer.ps1`：完整打包入口。
- `dist/installer/publish`：未经保护的主程序发布目录。
- `dist/installer/protected`：准备放入安装包的主程序目录。
- `dist/installer/obfuscated`：Obfuscar 输出目录。
- `dist/installer/uninstall-publish`：轻量卸载器发布结果。
- `dist/installer/setup-publish`：单文件安装器发布结果。
- `dist/setup/LittleFish_Setup_v版本号.exe`：最终交付安装包。

当前已有成品：

```text
dist/setup/LittleFish_Setup_v1.6.0.exe
```

当前成品大小约 `142.21 MiB`，并同时生成同名 `.sha256` 校验文件。

## 2. 打包流程

标准命令：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -Version 1.6.0
```

跳过混淆时：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -Version 1.6.0 -SkipObfuscation
```

脚本按以下顺序执行：

1. 删除旧的 `dist/installer` 内容、旧安装包和临时 `payload.zip`、`payload.sha256`。
2. 以 `Release + win-x64 + self-contained` 发布 LittleFish 主程序。
3. 主程序采用多文件发布，不生成单文件：

   ```text
   PublishSingleFile=false
   PublishReadyToRun=false
   DebugType=None
   DebugSymbols=false
   ```

4. 将发布目录完整复制到 `dist/installer/protected`。
5. 默认调用 Obfuscar 处理 `LittleFish.dll`，然后用混淆后的 DLL 替换 protected 目录中的原 DLL。
6. 发布 `LittleFish.Uninstaller` 为单文件、裁剪、自包含并启用单文件压缩的轻量卸载器。
7. 将轻量卸载器复制到 protected 目录，文件名为 `LittleFish_Uninstall.exe`。
8. 分别对 protected 目录中的 `LittleFish.exe` 和 `LittleFish_Uninstall.exe` 做烟测。
9. 删除烟测可能生成的 `settings.json`，避免把开发设置装进用户电脑。
10. 将 protected 目录压缩为：

   ```text
   src/LittleFish.Installer/Assets/payload.zip
   ```

11. 计算 payload 的 SHA-256，并将哈希与 payload 一起作为 WPF Resource 嵌入安装器。
12. 以 `Release + win-x64 + self-contained + single-file` 发布安装器：

    ```text
    PublishSingleFile=true
    IncludeNativeLibrariesForSelfExtract=true
    PublishReadyToRun=false
    EnableCompressionInSingleFile=true
    ```

13. 对单文件安装器执行版本比较与安装范围内置自检。
14. 将生成的 `LittleFish_Setup.exe` 复制并重命名为：

    ```text
    dist/setup/LittleFish_Setup_v版本号.exe
    ```

15. 生成最终安装包的同名 `.sha256` 文件。
16. 检查安装包、安装内容和轻量卸载器是否超过体积上限。
17. 删除源码目录中的临时 payload 文件；默认清理 `dist/installer`，需要排错时可传入 `-KeepBuildArtifacts`。

主程序和安装器都是 x64 自包含发布，因此目标电脑不需要预先安装 .NET 8 Desktop Runtime。

## 3. 混淆逻辑

打包脚本默认寻找：

```text
obfuscar.console
```

若未加入 PATH，则检查：

```text
%USERPROFILE%\.dotnet\tools\obfuscar.console.exe
```

未找到时打包直接失败，并提示：

```powershell
dotnet tool install -g Obfuscar.GlobalTool
```

当前 Obfuscar 配置启用：

- 隐藏私有 API；
- 隐藏字符串；
- 复用名称；
- `SuppressIldasm`；
- 保留公开 API。

`App`、`MainWindow`、`SettingsWindow` 三个主要 WPF 类型被大量跳过，以避免破坏 XAML 和事件绑定。因此目前属于基础混淆，不是强保护。

## 4. 安装器运行模式

同一个安装器 exe 有两种模式：

```text
普通启动                         安装 / 覆盖更新
--uninstall <安装目录>           卸载
/uninstall <安装目录>            卸载
```

安装器启动时先检查命令行：

- 找到卸载参数时进入卸载模式；
- 没有卸载参数时进入安装模式。

## 5. 全新安装逻辑

### 5.1 默认安装位置

默认父目录为：

```text
%LOCALAPPDATA%\Programs
```

安装器会自动追加 `LittleFish` 子目录，最终默认路径为：

```text
%LOCALAPPDATA%\Programs\LittleFish
```

用户选择其他目录时也会进行规范化：

- 选择 `D:\Tools`，最终为 `D:\Tools\LittleFish`；
- 选择 `D:\Tools\LittleFish`，不会重复追加；
- 选择磁盘根目录 `D:\`，最终为 `D:\LittleFish`。

这是当前用户级安装，默认路径不需要管理员权限。

### 5.2 安装界面

安装界面共三步：

1. 欢迎页；
2. 安装位置与快捷方式选项；
3. 安装进度与完成页。

默认勾选：

- 创建桌面快捷方式；
- 创建开始菜单快捷方式；
- 安装完成后启动 LittleFish。

### 5.3 文件安装

点击开始安装后：

1. 只查找安装目标目录下的 `LittleFish.exe` 进程。
2. 先调用 `CloseMainWindow()`；等待 1.8 秒仍未退出时强制结束整个进程树。
3. 创建安装目录。
4. 从安装器资源中把 `payload.zip` 释放到随机临时目录。
5. 逐项解压 payload 到安装目录。
6. 每个 ZIP 项目都经过目标路径校验，防止 `..` 等路径越界写出安装目录。
7. 如果目标文件与 ZIP 内容长度和字节完全一致，则跳过覆盖。
8. 文件被占用或无权写入时，转换为包含文件名和位置的明确错误提示。
9. 解压完成后删除临时 payload。

## 6. 快捷方式与卸载注册

桌面快捷方式：

```text
%USERPROFILE%\Desktop\LittleFish.lnk
```

开始菜单目录：

```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\LittleFish
```

其中包含：

```text
LittleFish.lnk
卸载 LittleFish.lnk
```

快捷方式通过 `WScript.Shell` 创建，目标、工作目录和图标都指向安装后的程序。

卸载信息写入当前用户注册表：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\LittleFish
```

主要字段：

- `DisplayName`
- `DisplayVersion`
- `Publisher`
- `InstallLocation`
- `DisplayIcon`
- `UninstallString`
- `NoModify=1`
- `NoRepair=1`

因此 LittleFish 会出现在 Windows 当前用户的“已安装的应用”列表中，不是全系统安装项。

## 7. 安装清单

安装完成后会在安装目录生成：

```text
.littlefish-manifest.txt
```

清单包括：

- 当前 payload 解压出的所有程序文件；
- `LittleFish_Uninstall.exe`；
- `.littlefish-manifest.txt` 本身。

用途：

- 覆盖更新时识别旧版本由安装器管理的文件；
- 删除新版已经不再分发的旧 DLL、EXE、配置和运行时文件；
- 避免旧版本残留长期堆积。

`settings.json` 不写入安装清单，也被旧版兼容清理逻辑明确排除，因此覆盖更新不会删除用户设置。

## 8. 覆盖更新逻辑

安装器启动时读取：

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\LittleFish
```

只要能读到 `InstallLocation`，就进入更新模式：

- 欢迎页改为“更新 LittleFish”；
- 显示已安装版本和即将安装的版本；
- 安装路径锁定为注册表中的原路径；
- 不允许重新选择目录；
- 关闭正在运行的 LittleFish；
- 覆盖相同文件；
- 根据旧安装清单清理新版不再包含的文件；
- 重建快捷方式；
- 重写卸载注册信息。

当前逻辑不比较新旧版本号。只要存在有效安装注册信息，即使运行相同版本或更旧的安装包，也会进入覆盖更新。

用户数据现状：

- `settings.json` 位于安装目录；
- 打包 payload 不包含 `settings.json`；
- 安装清单不管理 `settings.json`；
- 覆盖更新会保留 `settings.json`。

## 9. 卸载器生成逻辑

安装包 payload 中包含一个单独发布的轻量卸载器：

```text
LittleFish_Uninstall.exe
```

注册表和开始菜单通过以下参数调用它：

```text
LittleFish_Uninstall.exe --uninstall "<安装目录>"
```

安装目录中的卸载器启动后：

1. 把自己复制到随机临时目录；
2. 从临时目录重新启动，并附加 `--temp`；
3. 原进程退出；
4. 临时卸载器根据安装清单删除程序文件，从而避开“运行中的 exe 无法删除自身”的问题。

轻量卸载器不包含安装 payload，当前约 `10.47 MiB`。

## 10. 卸载逻辑

卸载时：

1. 校验卸载目录不为空且不是磁盘根目录。
2. 只查找安装目录下的 `LittleFish.exe` 进程并尝试关闭，1.5 秒后仍未退出则强制结束。
3. 根据当前用户或所有用户安装范围，删除对应桌面的 `LittleFish.lnk`。
4. 删除对应开始菜单中的 `LittleFish` 目录。
5. 删除 HKCU 或 HKLM 中对应的 LittleFish 卸载注册项。
6. 校验目标目录中存在 `.littlefish-manifest.txt`，否则停止卸载，防止明显误删。
7. 按安装清单删除程序文件、运行时文件、轻量卸载器和清单本身。
8. 当前用户安装默认保留 `%LOCALAPPDATA%\LittleFish\settings.json`；用户确认后才删除阅读设置、进度和书签。所有用户卸载不扫描其他用户的个人设置。
9. 将临时卸载器和临时目录登记为重启后删除。

正式安装版的设置位于 `%LOCALAPPDATA%\LittleFish`，便携版仍使用程序目录旁的 `settings.json`。从旧安装升级时会自动迁移一次。

## 11. 当前体积

当前 1.5.2 构建结果：

```text
主程序自包含发布目录       约 160.00 MiB
轻量卸载器                 约 10.47 MiB
安装内容总量               约 170.47 MiB
最终单文件安装包           约 142.21 MiB
```

安装时不会再把完整安装包复制为 `LittleFish_Uninstall.exe`。安装后大致存在：

- 约 160 MiB 的 LittleFish 主程序与 .NET 运行时；
- 约 10.47 MiB 的轻量卸载器；
- 少量配置、清单和快捷方式。

安装目录实际占用约 `170.47 MiB`。安装包本身比优化前的 `228.03 MiB` 减少约 `85.82 MiB`，降幅约 `37.6%`。

## 12. 当前已经做得比较完整的部分

- 主程序和安装器均为 x64 自包含发布，不依赖目标电脑的 .NET 安装状态。
- 安装路径会自动归一到独立的 `LittleFish` 子目录。
- payload 解压有路径越界保护。
- 更新先解压到同盘 staging 目录，失败时会从 rollback 目录恢复旧程序文件。
- 安装前校验 payload SHA-256，ZIP 解压仍有路径越界保护。
- 更新路径来自注册表，避免自定义路径用户重复安装到新位置。
- 版本比较区分升级、同版本修复和阻止降级。
- 安装清单可以清理旧版本冗余文件，更新保留并迁移用户设置。
- 默认路径保持当前用户安装；Program Files 等受保护位置会切换为所有用户安装并按需请求 UAC。
- 更新页面会读取现有快捷方式状态，完成时按勾选结果创建或删除。
- 卸载器为独立轻量程序，从临时目录运行，不依赖 PowerShell。
- 安装、更新和卸载只关闭安装目录下的 LittleFish，不影响 SeaS 内置版本或其他便携副本。
- 卸载默认保留 `settings.json`，用户可选择同时删除阅读设置、进度和书签。
- 卸载前会做基础安装目录特征校验。

## 13. 当前需要注意的问题

### 13.1 尚未配置代码签名

payload 和最终安装包已经生成 SHA-256，但当前仍没有可信发布证书。哈希可以检查下载或存储损坏，不能替代 Windows 代码签名的发布者身份验证。

### 13.2 所有用户卸载保留个人设置

所有用户安装的程序文件属于机器范围，但阅读设置属于各 Windows 用户。卸载器不会扫描并删除其他用户配置，避免跨用户误删。

### 13.3 WPF 主程序不做激进裁剪

主程序仍包含 WPF、WinForms 托盘和颜色选择依赖。为避免运行期反射或桌面组件缺失，本轮只压缩安装器和卸载器，不对主程序启用高风险裁剪。

## 14. 后续整理优先级建议

在继续使用这套独立安装器的前提下，建议依次处理：

1. 在管理员与标准用户账户中各做一次 Program Files 安装和卸载人工验收。
2. 为安装器服务层补充独立自动化测试项目。
3. 获取代码签名证书后，签署主程序、卸载器和最终安装包。
