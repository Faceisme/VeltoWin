# Velto for Windows

Velto 的 Windows 11 版本,目前只实现鼠标手势功能。是 macOS 版 [Velto](../Velto) 的 Windows 移植子集。

## 功能

- 按住鼠标右键拖动 → 绘制手势轨迹
- 松开右键 → 识别手势并触发对应快捷键
- 普通右键单击不受影响,仍会弹出系统右键菜单
- 手势绘制中显示可选轨迹反馈
- 手势超时自动取消
- 每个手势可以录入多个样本提升容错
- 支持把动作发送到「光标下窗口」或「当前活动窗口」
- 配置导入 / 导出 (JSON)

## 默认手势

| 手势 | 默认快捷键 | 用途 |
| --- | --- | --- |
| 向左 | `Alt+←` | 浏览器后退 |
| 向右 | `Alt+→` | 浏览器前进 |
| 向上 | `Ctrl+T` | 新建标签页 |
| 向下 | `Ctrl+W` | 关闭标签页 |

## 开发环境

- Windows 11 x64
- .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)

## 构建

```powershell
# Debug 构建并运行
dotnet run --project src

# Release 单文件 exe
.\build.ps1
# 输出: publish\Velto.exe
```

## 项目结构

```
src/
  Velto.csproj
  App.xaml / App.xaml.cs       入口,创建托盘 + 启动手势引擎
  Models/                       数据模型(Shortcut / GestureCommand / AppPreferences)
  Services/
    ConfigStore.cs             配置 JSON 持久化 (%APPDATA%\Velto\config.json)
    GestureDirection.cs        方向签名算法 — 拐角分段 + 弧向度量
    GestureRecognizer.cs       识别算法 — 命令级 canonical 签名 + 最近邻匹配
    MouseHook.cs               全局低层鼠标钩子 (WH_MOUSE_LL)
    KeyboardSender.cs          快捷键合成 (SendInput)
    WindowTargeter.cs          目标窗口定位 (光标下 vs 活动)
    GestureEngine.cs           手势生命周期状态机
  Win32/NativeMethods.cs       Win32 P/Invoke 集中点
  UI/
    TrailOverlayWindow         手势轨迹覆盖层 (透明分层窗口)
    TrayIcon                   系统托盘
    SettingsWindow             设置主窗口
    GestureEditorView          单个手势编辑(样本 + 快捷键)
```

## 权限

Windows 不像 macOS 需要单独授权辅助功能,低层鼠标钩子只需:

- 普通用户即可安装 `WH_MOUSE_LL` 钩子
- 如果想向以管理员权限运行的窗口(如某些系统设置)发送按键,Velto 自身也需要管理员权限。日常浏览器/编辑器/资源管理器场景不需要。

## 注意

- 全局右键钩子在手势进行中会暂时吞掉右键事件,普通右键单击通过 SendInput 重新合成,以确保系统右键菜单仍能弹出。
- 手势识别沿用 macOS 版的方向签名方案(GestureDirection.swift / GestureRecognizer.swift),识别结果应当一致。
