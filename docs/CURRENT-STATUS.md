# Curtain 当前开发与发布状态

更新：2026-11-03。Curtain `0.2.0-beta.5.5` 已由用户验收并公开发布。发布源码提交为 `19f1c70535f0177fe3f6368e1f05a17ac1ba7050`，标签 `v0.2.0-beta.5.5` 固定在该提交；GitHub [预发布](https://github.com/Cheney-0821/Curtain/releases/tag/v0.2.0-beta.5.5) 与 R2 更新源均已核验。官网 `/download/` 动态跳转至 beta.5.5 安装器。beta.5.4 历史发布记录见下文。

## 2026-11-02–03 beta.5.5 发布与验收

- 唯一工作区与分支：`D:\Cheney\Codex Project\Curtain` / `main`。保留 Global-R6 已接受的单窗口 Backdrop + Composition + Win2D Gaussian 路线、模糊档位/全局默认/逐窗覆盖和现有全屏 Acrylic 路线。
- 已对点击穿透 HWND 样式变更加原生刷新及失败回滚；会话与遮罩初始化诊断记录目标 HWND/PID、异常类型与 HResult，不记录窗口标题或内容。
- 新增并核对 Win2D、Windows Community Toolkit 许可证和归属；发行包目录结构改为 `licenses/third-party/...`，保留 32 份第三方 LICENSE/NOTICE 原文和来源哈希。Win2D NuGet 旧 `licenseUrl` / `requireLicenseAcceptance` 元数据差异继续如实作为非阻塞跟进项，未声称已经法律澄清。
- 自动门禁：`scripts/verify.ps1` 退出码 0；Core 103 项通过，Debug/Release x64 与 Release Windows Fixture 构建均为 0 警告、0 错误。`scripts/static-check.py`、`git diff --check` 通过。正式 `dotnet publish` 与 Velopack 包装退出码 0；Setup、full、delta、portable 均已生成。
- r4 Release 候选位于 `artifacts/B3/review-beta55-20261103-expander-cards-checkbox-r4/`。用户确认资源管理器行展开正常，三个设置卡铺满，“使用全局设置”复选框符合预期，并反馈功能全部正常。r2 闪退和 r3 窄栏布局问题分别记录于 `reports/beta55/EXPANDER-CRASH-FIX-20261103.md`，均已由后续修复解决。
- 用户验收候选使用独立 `test-settings/`。没有直接启动无隔离设置目录的正式 publish EXE，也未在当前 Windows 用户配置上执行 beta.5.4→5.5 安装升级/卸载；这些项目明确列为 **Not tested**。旧 beta.5.4 安装测试目录保持原样。
- 用户确认 r4 候选功能全部正常，资源管理器逐窗卡片展开及“使用全局设置”复选框符合预期；该用户验收通过不替代未执行的安装升级/卸载。
- GitHub 源码提交：`19f1c70535f0177fe3f6368e1f05a17ac1ba7050`；标签 `v0.2.0-beta.5.5` 解引用到同一提交；已正常推送 `origin/main` 与标签。beta.5.4 标签和 Release 未修改。
- GitHub 预发布：<https://github.com/Cheney-0821/Curtain/releases/tag/v0.2.0-beta.5.5>；上传 Setup、Full、Delta、Velopack feed、`RELEASES` 和 SHA-256 清单。GitHub Setup 回读 SHA-256 与本地产物相同：`352E6620D43A11A3DF7CEB404E3C4D294EE85BAE0773E4CDEA7293B5915FDDDF`。
- R2 feed：<https://curtain-updates.cheneylab.cn/beta/releases.win.json>，HTTP 200，15 条记录（beta.5.5 两条新包加 beta.5.4 已发布快照中的 13 条历史记录）；feed SHA-256 `BAA21970E9FA90802A1E10B647CFD3189BA07AEF15D508D77CCE227555695435`，Content-Length 5150。15 个 feed 包对象均 HTTP 200 且大小与 feed 一致；新 Setup、Full、Delta 均从公网完整下载并与本地产物 SHA-256 匹配。
- 发布时发现打包目录内的初始 feed 缺少历史条目；发布前从 beta.5.4 归档快照恢复原有 13 条记录并加入 beta.5.5 后，再次上传并核验最终 feed。旧版本对象未删除或覆盖。
- 官网 `https://curtain.cheneylab.cn/download/` 返回 HTTP 302，`Location` 指向 `CurtainPrivacy-0.2.0-beta.5.5-Setup.exe`；目标安装器 HTTP 200。官网源码未修改或部署。
- 安装器未签名；beta.5.4→5.5 安装升级/卸载、Windows 10、RDP、HDR、混合 DPI、热插拔、GPU 设备丢失恢复和长时性能矩阵为 **Not tested**。Win2D NuGet 旧 licenseUrl / `requireLicenseAcceptance` 元数据差异仍是非阻塞跟进项，未声称法律澄清。

正式发布产物哈希、完整测试矩阵和证据已收录于本轮交付 ZIP；旧版发布记录保留。

## 2026-10-28 beta.5.4 发布范围

R3 修复已合入本次发行源码。最小化、700×600 默认窗口及设置对齐获用户通过。全屏遮罩会在通知、弹窗或任务栏改变层级后尝试恢复；用户反馈仍偶发有弹窗短暂超出模糊，但可接受且不阻塞发布。此版本不承诺持续覆盖所有系统界面，也不防截图或录屏。

- GitHub：[v0.2.0-beta.5.4](https://github.com/Cheney-0821/Curtain/releases/tag/v0.2.0-beta.5.4)，公开 Pre-release；安装包 SHA-256：`779C502FBEC203587B387C3FFD3769BFE31FCA877E3D235F3DA2649E879C2E90`。
- R2：`releases.win.json` 共 13 个资产条目，包含新 Full/Delta 与原有 11 条历史记录；匿名索引请求 200、Setup Range 请求 206，完整 Setup 下载哈希一致。
- 官网中英文首页均返回 200 并使用动态 `/download/`；该路由当前重定向至 beta.5.4 Setup。官网源码因含用户未提交修改，本次未改动或部署。
- 自动门禁：static-check、Debug/Release x64、Core 82 项、Windows Fixture 与真实 HWND Z-order 检查通过。
- 发布版直接启动、安装/卸载与旧版本升级：Not tested。当前环境无法提供隔离用户配置的 Windows profile；为保护用户真实设置，没有启动 Release EXE。

后续版本发布时，请保留本节记录并在最上方标明新的公开版本。

## 仓库与工作流

- 唯一受维护的源代码工作区为本仓库的工作根目录；开发、测试、构建和发布均从同一工作区执行。
- 唯一长期开发分支：`main`；GitHub `Cheney-0821/Curtain` 的 `main` 为公开源代码历史。
- 本地机密、内部日志、旧版研究和测试证据不作为公共源码提交内容。

## Beta5.3 候选与验收

- Beta5.3 候选包含单窗口及全屏遮罩约 180ms 的整体 Alpha 渐变，并修正首次添加时渐变的起始时机；动画改变的是 Acrylic 遮罩窗口透明度，不是模糊半径。
- 用户确认首次添加、再次添加、普通开合、全屏开合及快速反向均无闪白，动画自然。
- 用户确认以下主源候选功能检查均通过：中英文切换；取消/无效选窗及关闭目标后重新选窗；解除遮罩后点击和键盘输入恢复；托盘退出后窗口释放且无残留进程。
- 已移除模糊强度档位，保留旧设置字段仅用于数据兼容；当前不支持动态调节 Acrylic 模糊强度。

| 项目 | 状态 | 依据 |
|---|---|---|
| 单窗口、全屏与首次渐入视觉表现 | 用户通过 | R4A 候选及后续主源候选实测反馈 |
| 中英文界面 | Pass | 用户确认主源候选检查通过 |
| 取消/无效选窗及目标关闭后的恢复 | Pass | 用户确认主源候选检查通过 |
| 解除遮罩后的鼠标和键盘输入 | Pass | 用户确认主源候选检查通过 |
| 托盘退出、窗口释放及无残留进程 | Pass | 用户确认主源候选检查通过 |
| Windows 动画关闭回退 | Not tested | 设备专项未测；用户知悉并接受该发布限制 |
| DPI 缩放、跨屏、热插拔及多屏同步 | Not tested | 设备专项未测；用户知悉并接受该发布限制 |
| Windows 10 独立实机兼容 | Not tested | 目前主要在 Windows 11 上验证 |
| beta.5.2 至 beta.5.3 的线上更新 | Not verified | 更新抽检未观察到客户端暂存新包；本机到公开更新域名的 TLS 请求失败，不能据此认定更新功能通过或产品缺陷 |

## 自动验证与安装包

- 自动检查、Debug/Release x64、Core 82 项、Windows Fixture、最终发布构建和隔离安装生命周期均已通过；隔离安装包可启动并卸载。
- 自动测试和构建仅证明对应静态、核心及打包检查，不代替用户报告的界面体验，也不覆盖未测设备专项。
- 安装包未签名，Windows 可能提示未知发布者。

## 发布状态

- 目标版本：`0.2.0-beta.5.3`；Git 标签：`v0.2.0-beta.5.3`；GitHub 采用预发布。
- GitHub 预发布：[v0.2.0-beta.5.3](https://github.com/Cheney-0821/Curtain/releases/tag/v0.2.0-beta.5.3)，源码提交 `21c8fe8c4d67829f8ae1b90bd2606e43b9ec7bbf`，标签 `v0.2.0-beta.5.3` 固定在该提交。
- Setup SHA-256：`6CFA25E8CEEA254DB8C5793FC1DEAA9FCC5E5E1FAE292083448431FECF7315D1`；GitHub 下载资产摘要与本地包一致。R2 版本对象和索引已上传，Cloudflare API 元数据、大小及 ETag 校验通过，历史版本条目保留；本机公网 TLS 握手失败，因此未声称完成匿名公网 HTTP 抽查。
- beta.5.2 客户端原位升级未验证；公开说明已披露该边界。beta.5.2 仍可从旧版本对象获取，未删除或改写旧标签/包。
- beta.5.2 与更早版本的 Tag、Release 和 R2 版本对象保持不变。
