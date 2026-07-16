## 简介

LittleFish 使用 C#、WPF 和 .NET 8 开发，适合快速打开本地 TXT 文件，并在桌面角落以较低干扰的方式阅读。

它是一款只读阅读器，不会修改或覆盖原始 TXT 文件，也不提供账号、云同步、广告或阅读数据上传功能。

- 当前版本：`1.5.2`
- 支持系统：Windows 10/11 x64
- 项目状态：稳定维护；后续以兼容性修复和必要维护为主

## 下载与安装

前往 [Releases](https://github.com/starash07/LittleFish/releases) 下载最新的 `LittleFish_Setup_v版本号.exe`。

安装包为自包含版本，普通用户不需要提前安装 .NET 运行时。安装程序支持：

- 自定义安装位置；
- 创建桌面和开始菜单快捷方式；
- 安装完成后启动 LittleFish；
- 检测已有版本并执行升级或修复；
- 阻止使用旧安装包直接覆盖较新的已安装版本。

当前安装包没有商业代码签名证书，Windows 可能显示“未知发布者”。建议同时下载 Release 中的 SHA-256 校验文件核对安装包完整性。

## 主要功能

### TXT 阅读

- 打开或拖入本地 TXT 文件；
- 记录最近文件、上次文件和阅读位置；
- 识别 UTF-8、UTF-8 BOM、GB18030、Big5 等常见编码；
- 支持鼠标滚轮、逐行翻动、整页翻页和自动翻页；
- 支持章节识别、章节跳转、全文查找和书签；
- 支持进度显示与指定进度跳转；
- 正文区域只读，不修改原始文件。

### 阅读外观

- 自定义字体、字号、文字颜色和背景颜色；
- 分别调整背景不透明度与整窗不透明度；
- 隐藏功能栏后仍可拖动和缩放窗口；
- 长文本按当前可见页渲染，减少窗口缩放和翻页时的卡顿。

### 桌面行为

- 窗口置顶；
- 鼠标移出后自动隐藏或降低可见度；
- 最小化到系统托盘；
- 快捷隐藏与恢复窗口；
- 可自定义主要功能的快捷键；
- 固定的强制显示快捷键，用于找回过度透明或隐藏的窗口。

## 快捷键

大部分快捷键可以在“设置 → 快捷键”中修改或清空。

| 默认快捷键 | 功能 |
| --- | --- |
| `Ctrl+O` | 打开 TXT |
| `Ctrl+,` | 打开设置 |
| `Ctrl+F` | 查找 |
| `F3` | 查找下一个 |
| `Ctrl+B` | 添加书签 |
| `F8` | 打开章节窗口 |
| `F7` | 显示或隐藏功能栏 |
| `Ctrl+T` | 开启或关闭置顶 |
| `F9` | 开启或关闭悬隐 |
| `F6` | 开启或关闭自动翻页 |
| `Ctrl+Shift+Z` | 快捷隐藏或恢复窗口 |
| `Ctrl+Plus` / `Ctrl+Minus` | 增大或减小字号 |
| `Home` / `End` | 跳到开头或末尾 |
| `PageUp` / `PageDown` | 上一页或下一页 |
| `Ctrl + 鼠标滚轮` | 调整背景不透明度 |
| `Alt + 鼠标滚轮` | 调整整窗不透明度 |

以下快捷键固定保留：

- `Ctrl+Shift+F12`：强制显示窗口，并恢复可见度；
- `Ctrl+W`：关闭 LittleFish。

## 隐私与本地数据

LittleFish 不需要账号，阅读和设置数据均保存在本机。

- 正式安装版本：`%LOCALAPPDATA%\LittleFish\settings.json`
- 便携或开发运行：程序目录下的 `settings.json`

设置文件包含阅读偏好、最近文件、阅读进度和书签。卸载程序默认保留这些数据，以便重新安装后继续使用。

## 从源码运行

开发环境：

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Visual Studio 2022、Rider 或 VS Code（可选）

在项目根目录运行：

```powershell
dotnet restore .\LittleFish.sln
dotnet build .\LittleFish.sln -c Debug --no-restore
dotnet run --project .\src\LittleFish.App\LittleFish.App.csproj
```

也可以先检查本机开发环境：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\check-environment.ps1
```

## 构建安装包

正式打包需要安装 Obfuscar：

```powershell
dotnet tool install -g Obfuscar.GlobalTool
```

生成 `1.5.2` 安装包：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1 -Version 1.5.2
```

脚本会依次完成：

1. 发布 Windows x64 自包含应用；
2. 对 LittleFish 自有程序集执行基础混淆；
3. 运行主程序和卸载器烟雾测试；
4. 构建自定义单文件安装器；
5. 生成安装包及 SHA-256 校验文件。

最终文件位于：

```text
dist/setup/
├── LittleFish_Setup_v1.5.2.exe
└── LittleFish_Setup_v1.5.2.exe.sha256
```

仅定位打包兼容性问题时，可以使用 `-SkipObfuscation`；它不应作为正式发布方式。

## 项目结构

```text
LittleFish/
├── LittleFish.sln
├── docs/                         # 安装与打包设计文档
├── scripts/                      # 环境检查、运行和打包脚本
└── src/
    ├── LittleFish.App/           # WPF 阅读器主程序
    ├── LittleFish.Installer/     # 自定义安装器
    └── LittleFish.Uninstaller/   # 轻量卸载器
```

构建产物、安装包和本地设置均由 `.gitignore` 排除，不应提交到源码仓库。

## 与 SeaS 的关系

LittleFish 可以作为独立应用安装和运行。SeaS 会随自身版本内置一份固定的 LittleFish 运行组件，但两个项目拥有独立的源码、安装流程和发布版本。
