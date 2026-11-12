# Curtain 0.3.0 — Stable Release

## 简体中文

**Curtain 0.3.0 是首个正式稳定版。** 本次版本统一了 Windows 10 和 Windows 11 的窗口/全屏隐私遮罩渲染方式，两种系统继续使用同一个 Windows x64 安装包。

### 本次改进

- **统一高斯模糊渲染**：窗口保护和全屏保护采用同一套 Compositor Backdrop + GaussianBlurEffect 实现，移除旧全屏 Acrylic 渲染路径。
- **Windows 10 兼容修复**：只在支持的 Windows 11 系统上调用特定 DWM 属性；在 Windows 10 22H2 上已验证窗口选择与添加、模糊效果和全屏遮罩。
- **全屏恢复操作**：采用原生胶囊式恢复控制，完善显示、隐藏、悬停和按压动画；动画可独立于 Windows 系统动画设置运行。
- **鼠标侧键稳定性**：修复部分环境中完整鼠标侧键输入被重复识别而导致全屏保护快速切换的问题，同时保留首次有效点击的即时响应。
- **保护与设置体验**：改善记忆窗口绑定失败重试，整理输入快捷键设置，保留既有自定义组合键。
- **版本与诊断**：发布 `0.3.0` 正式版本及可追溯构建身份，诊断反馈显示正确产品版本。

### 兼容性与说明

- 最低支持 Windows 10 Build 19041；本次验证环境为 Windows 10 22H2（Build 19045）与 Windows 11 25H2。
- 本版本为**稳定版**，非 Beta / Pre-release。
- 安装包暂未代码签名，Windows 可能提示未知发布者。
- Curtain 是视觉隐私遮罩，无法替代系统锁屏，也不阻止截图、录屏等操作。

## English

**Curtain 0.3.0 is the first stable release.** Window and fullscreen privacy overlays now share one Gaussian blur implementation across Windows 10 and Windows 11, delivered in a single Windows x64 installer.

### What's new

- **Unified Gaussian rendering:** Selected-window and fullscreen protection share the Compositor Backdrop + GaussianBlurEffect implementation. The former fullscreen Acrylic rendering path has been retired.
- **Windows 10 compatibility:** Windows 11-specific DWM attributes are called only on supported OS builds. Window selection, attaching overlays, blur, and fullscreen protection have been validated on Windows 10 22H2.
- **Native fullscreen restore control:** A compact floating capsule with reveal, hide, hover, and press transitions that remain functional when Windows system animations are disabled.
- **More reliable mouse side buttons:** Filters duplicate complete side-button pulses that could cause rapid protection reversals while preserving immediate response to the initial valid press/release.
- **Protection and settings refinements:** Smarter retry pacing for remembered-window attachment, consolidated shortcut controls, and preservation of existing custom hotkeys.
- **Version and diagnostics:** Full `0.3.0` product version and traceable build information in diagnostics.

### Compatibility and known limits

- Minimum supported Windows 10 build: 19041. Validated on Windows 10 22H2 (19045) and Windows 11 25H2.
- This is a **stable** GitHub Release, not a prerelease.
- The installer is currently unsigned; Windows may display an unknown publisher warning.
- Curtain provides a visual privacy overlay, not a Windows security lock or protection against screenshots and screen recording.

Clean-build source commit, download sizes, SHA-256 checksums, and signature status are published with the GitHub Release assets.
