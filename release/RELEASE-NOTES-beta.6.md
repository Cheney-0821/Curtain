# Curtain 0.2.0-beta.6

## 简体中文

### 新增与改进

- **记忆常用应用：** 保存应用保护规则。重启 Curtain 或 Windows 后规则仍保留；应用再次启动时可自动重新绑定窗口。
- **全屏保护交互：** 恢复控件默认隐藏；鼠标进入当前屏幕上方四分之一区域可唤出，离开区域或静止 1 秒后淡出。
- **全屏快捷键：** 默认 `Ctrl+↓` 切换全屏保护；`Alt+S` 开始选窗。两组快捷键均可自定义。
- **鼠标侧键：** 可选后退键或前进键作为全屏保护开关，默认关闭。
- **诊断与反馈：** 本地保留诊断记录，可生成诊断 ZIP 并在确认后主动提交；上传失败时本地 ZIP 保留。
- **多窗口管理：** 按应用整理记忆规则与运行窗口，并保留逐窗保护设置。

### 已知限制

- 安装包未进行代码签名，Windows 可能提示未知发布者。
- Curtain 是视觉隐私遮罩，不是系统锁屏，也不能阻止截图或录屏。
- 标准用户环境下原生 ETW 跟踪返回 `AdministratorRequired`，目前不能视为可用采集能力。
- 个别系统通知或短暂弹窗仍可能短暂显示在全屏遮罩上方。

### 默认快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+↓` | 切换全屏保护 |
| `Alt+S` | 开始选择窗口 |

鼠标后退/前进键可选作全屏保护开关，默认关闭。用户保存的自定义快捷键会继续保留。

## English

### What's new

- **Remembered applications:** Save protection rules across Curtain and Windows restarts; matching windows can be rebound when an app starts again.
- **Fullscreen controls:** The restore control starts hidden. Move the pointer into the upper quarter of the current display to reveal it; it fades after one second outside the area or without pointer movement.
- **Fullscreen shortcut:** `Ctrl+Down` toggles fullscreen protection by default. `Alt+S` starts window selection. Both shortcuts are customizable.
- **Mouse side buttons:** Optionally assign Back or Forward to toggle fullscreen protection. Off by default.
- **Diagnostics and feedback:** Keep local diagnostics, create a ZIP, and submit it only after consent. A failed upload leaves the ZIP on the device.
- **Multi-window management:** Organize remembered app rules and running windows while preserving per-window protection settings.

### Known limitations

- The installer is unsigned; Windows may show an unknown-publisher warning.
- Curtain is a visual privacy overlay, not a sign-in lock or protection against screenshots or recording.
- Native ETW tracing currently reports `AdministratorRequired` in the standard-user environment and is not considered available.
- Some system notifications or transient popups may briefly appear above the fullscreen overlay.

### Default shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Down` | Toggle fullscreen protection |
| `Alt+S` | Start window selection |

The Back/Forward mouse buttons can optionally toggle fullscreen protection; Off is the default. Saved custom shortcuts are preserved.

## Release artifacts

The exact source commit, installer SHA-256, and downloadable assets are listed in the [GitHub Pre-release](https://github.com/Cheney-0821/Curtain/releases/tag/v0.2.0-beta.6).
