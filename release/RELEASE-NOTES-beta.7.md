# Curtain 0.2.0-beta.7

## 简体中文

### 诊断与可追溯性改进

- **完整版本身份：** 诊断信息现在保留完整产品版本（例如 `0.2.0-beta.7`），并记录数字文件版本、Git Commit、Build ID 和构建工作树状态。
- **应用运行会话：** 每次启动生成独立的应用会话 ID；新写入的结构化诊断日志自动附带版本、构建和会话身份，同时保留原有业务 `sessionId` 语义。
- **Windows 历史崩溃归属：** 区分事件发生时间、数字文件版本和可验证的进程信息。无法唯一确认的旧版本事件会明确标记为 `LegacyAmbiguous` 或 `Unknown`，避免把历史问题误判成当前版本回归。
- **安装类型识别：** 改进 MSIX 包身份与 Velopack 安装检测顺序；无法确认的原生 API 错误会保留为 `Unknown`，避免错误分类。
- **诊断格式：** 诊断摘要、环境快照和 manifest 使用一致的 Schema v2 版本身份；旧版日志仍可兼容读取。

beta.7 主要改进诊断信息的准确性与问题追踪能力，没有刻意改变窗口保护、全屏保护、快捷键、模糊效果或点击交互。

### 已知限制

- 安装包尚未进行代码签名，Windows 可能提示未知发布者。
- Curtain 是视觉隐私遮罩，不是 Windows 锁屏，也不能阻止截图或录屏。
- 验证主要在 Windows 11 完成；Windows 10、混合 DPI、显示器热插拔和复杂多显示器环境仍需独立实机验证。
- 标准用户环境下原生 ETW 跟踪可能返回 `AdministratorRequired`，不能视为已成功采集。
- 个别系统通知或短暂弹窗仍可能短暂显示在全屏遮罩之上。
- 真实 MSIX 安装、管理员 ETW、故意制造崩溃及完整 GUI 回归矩阵未在本轮单独验证。

## English

### Diagnostics and traceability

- **Complete version identity:** Diagnostic data now preserves the full product SemVer (for example, `0.2.0-beta.7`) alongside the numeric file version, Git commit, build ID, and working-tree state.
- **Application sessions:** Each app launch receives a distinct session ID. New structured diagnostic entries automatically include version, build, and app-session metadata while preserving the existing business `sessionId` semantics.
- **Historical Windows crash attribution:** Event time, numeric file version, and verifiable process information are evaluated separately. Events that cannot be uniquely attributed are marked `LegacyAmbiguous` or `Unknown`, avoiding false reports of current-version regressions.
- **Install-kind detection:** MSIX package identity is checked before Velopack installation detection. Unrecognized native API errors remain `Unknown` instead of being misclassified.
- **Diagnostic schema:** The diagnostic summary, environment snapshot, and manifest share a consistent Schema v2 version identity. Older structured logs remain readable.

Beta.7 focuses on diagnostic accuracy and traceability. It does not intentionally change window protection, fullscreen protection, shortcuts, blur behavior, or click interaction.

### Known limitations

- The installer is unsigned; Windows may show an unknown-publisher warning.
- Curtain is a visual privacy overlay, not a Windows sign-in lock or protection against screenshots or recording.
- Validation has primarily been performed on Windows 11. Windows 10, mixed-DPI, monitor hot-plug, and complex multi-display setups still need separate real-device validation.
- Native ETW tracing may report `AdministratorRequired` in a standard-user environment and must not be treated as successfully captured.
- Some system notifications or transient popups may briefly appear above the fullscreen overlay.
- A real MSIX installation, administrator-level ETW capture, intentionally induced crash, and the full GUI regression matrix were not separately validated in this release cycle.

## Release artifacts

The source commit, installer SHA-256, and verified download assets are recorded in the GitHub Release.
