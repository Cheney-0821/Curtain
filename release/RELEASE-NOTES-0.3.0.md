# Curtain 0.3.0 — Stable Release

## 简体中文

**Curtain 0.3.0 是首个正式稳定版。** 本次版本统一了 Windows 10 和 Windows 11 的窗口/全屏隐私遮罩渲染方式，两种系统继续使用同一个 Windows x64 安装包。

### 本次改进

- **统一高斯模糊渲染**：窗口保护和全屏保护采用一致的模糊效果。
- **Windows 兼容性**：Windows 10 与 Windows 11 使用同一个 Windows x64 安装包。
- **全屏恢复操作**：采用原生胶囊式恢复控制，完善显示、隐藏、悬停和按压动画；动画可独立于 Windows 系统动画设置运行。
- **快捷操作**：改进鼠标侧键和快捷键操作体验。
- **保护与设置体验**：改进记忆窗口保护，并整理快捷键设置，保留既有自定义组合键。
- **版本与诊断**：发布 `0.3.0` 正式版本及可追溯构建身份，诊断反馈显示正确产品版本。

### 兼容性与说明

- 最低支持 Windows 10 Build 19041。
- 本版本为**稳定版**，非 Beta / Pre-release。
- 安装包暂未代码签名，Windows 可能提示未知发布者。
- Curtain 是视觉隐私遮罩，无法替代系统锁屏，也不阻止截图、录屏等操作。

## English

**Curtain 0.3.0 is the first stable release.** Window and fullscreen privacy overlays now share one Gaussian blur implementation across Windows 10 and Windows 11, delivered in a single Windows x64 installer.

### What's new

- **Unified Gaussian rendering:** Window and fullscreen protection use a consistent blur effect.
- **Windows compatibility:** Windows 10 and Windows 11 use the same Windows x64 installer.
- **Native fullscreen restore control:** A compact floating capsule with reveal, hide, hover, and press transitions that remain functional when Windows system animations are disabled.
- **Shortcuts and quick controls:** Improved the mouse side-button and keyboard shortcut experience.
- **Protection and settings refinements:** Improved remembered-window protection and consolidated shortcut controls while preserving existing custom hotkeys.
- **Version and diagnostics:** Full `0.3.0` product version and traceable build information in diagnostics.

### Compatibility and notes

- Minimum supported Windows 10 build: 19041.
- This is a **stable** GitHub Release, not a prerelease.
- The installer is currently unsigned; Windows may display an unknown publisher warning.
- Curtain provides a visual privacy overlay, not a Windows security lock or protection against screenshots and screen recording.

Clean-build source commit, download sizes, SHA-256 checksums, and signature status are published with the GitHub Release assets.
