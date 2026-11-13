# Curtain 0.3.1 — Stable Release

## 简体中文

**Curtain 0.3.1 正式稳定版。** 本次更新改进了应用窗口自动绑定判断，并优化了遮罩边缘与阴影表现。Windows 10 与 Windows 11 使用同一个 Windows x64 安装包。

### 本次改进

- **Explorer 临时弹窗识别优化：** 地址栏候选框、右键菜单、二级菜单和 Alt 键位提示等临时界面不会再被单独绑定为受保护窗口；Explorer 主窗口仍可正常自动绑定。
- **遮罩边缘与阴影优化：** 减少遮罩与目标窗口阴影叠加，保持 Windows 11 圆角与 Windows 10 原生直角表现。
- **统一安装包：** Windows 10 与 Windows 11 共用同一个 Windows x64 安装包。

### 兼容性与说明

- 支持 Windows 10 Build 19041 及更高版本，以及 Windows 11。
- 安装包未进行代码签名，Windows 可能显示未知发布者提示。
- Curtain 是视觉隐私遮罩，不是系统锁屏，也不能阻止截图或录屏。

## English

**Curtain 0.3.1 is a stable release.** This update improves automatic window binding and refines overlay edges and shadows. Windows 10 and Windows 11 use the same Windows x64 installer.

### Improvements

- **Improved transient Explorer popup handling:** Address bar suggestions, context menus, submenus, and Alt Key Tips are not bound as separate protected windows. Explorer main windows continue to bind automatically.
- **Refined overlay edges and shadows:** Reduces shadow overlap between the overlay and its target while preserving rounded corners on Windows 11 and native square corners on Windows 10.
- **One installer:** Windows 10 and Windows 11 share the same Windows x64 installer.

### Compatibility and notes

- Supports Windows 10 Build 19041 and later, and Windows 11.
- The installer is not code-signed; Windows may show an unknown publisher warning.
- Curtain is a visual privacy overlay. It is not a system lock screen and does not prevent screenshots or screen recording.
