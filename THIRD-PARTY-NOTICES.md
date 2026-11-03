# Curtain 第三方许可与 NOTICE

Curtain 自有代码的许可见根目录 LICENSE。第三方组件保留各自许可和 NOTICE 原文；候选中归档的 32 份原文位于 licenses/third-party/。来源、包内路径和 SHA-256 见 licenses/third-party/SOURCES.md。

| 组件 | 版本 | 随附原文 |
|---|---|---|
| Microsoft.WindowsAppSDK | 2.5.1 | license.txt、NOTICE.txt |
| Microsoft.WindowsAppSDK.Base | 2.0.4 | license.txt、NOTICE.txt |
| Microsoft.WindowsAppSDK.Runtime | 2.5.1 | license.txt、NOTICE.txt |
| Microsoft.WindowsAppSDK.AI | 2.5.5 | license.txt |
| Microsoft.WindowsAppSDK.DWrite | 2.1.0 | license.txt |
| Microsoft.WindowsAppSDK.Foundation | 2.3.12 | license.txt |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.9 | license.txt |
| Microsoft.WindowsAppSDK.ML | 2.1.94 | license.txt |
| Microsoft.WindowsAppSDK.Search | 2.5.5 | license.txt |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 | license.txt |
| Microsoft.WindowsAppSDK.WinUI | 2.3.9 | license.txt、NOTICE.txt、tools/NOTICE.txt |
| Microsoft.Windows.AI.MachineLearning | 2.1.74 | license.txt、ThirdPartyNotices.txt |
| Microsoft.Web.WebView2 | 1.0.3719.77 | LICENSE.txt、NOTICE.txt |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.9 | LICENSE.TXT、THIRD-PARTY-NOTICES.TXT |
| System.Numerics.Tensors | 9.0.0 | LICENSE.TXT、THIRD-PARTY-NOTICES.TXT |
| Velopack | 1.2.161 | LICENSE |
| Microsoft.Graphics.Win2D | 1.4.0 | LICENSE.txt（上游源文件；NuGet 元数据差异见下文） |
| CommunityToolkit.WinUI.Controls.SettingsControls | 8.2.251219 | License.md |
| CommunityToolkit.Common | 8.2.1 | License.md、ThirdPartyNotices.txt |
| CommunityToolkit.WinUI.Extensions | 8.2.251219 | License.md |
| CommunityToolkit.WinUI.Helpers | 8.2.251219 | License.md |
| CommunityToolkit.WinUI.Triggers | 8.2.251219 | License.md |

Microsoft.Windows.SDK.NET.Ref 10.0.26100.57 的包未提供本地许可原文；对应许可入口为 https://aka.ms/WinSDKLicenseURL。包版本与官方包来源见源码依赖清单。

### Win2D 1.4.0 元数据差异

本仓库随附的 `licenses/third-party/microsoft.graphics.win2d/1.4.0/LICENSE.txt` 是 Microsoft/Win2D 官方仓库在 1.4.0 发布准备提交 `2cd2e846b0b410286a88fa5cddc3bc765de8be0b` 中的 MIT 文本。实际恢复的 `Microsoft.Graphics.Win2D` 1.4.0 NuGet 包则记录 `requireLicenseAcceptance=true` 和旧 `licenseUrl=http://www.microsoft.com/web/webpi/eula/eula_win2d_10012014.htm`；该 URL 当前跳转到通用 Microsoft Learn 页面，无法从该入口读取原 EULA。NuGet 包内没有 LICENSE 或 NOTICE，包内 `Win2d.githash.txt` 所记的 `57e06e2c24703526f500035129251d063881a44d` 也未能在上游 GitHub 定位。因此，上游 MIT 文本用于归档当前可核验的上游许可证材料，不能据此宣称 NuGet 包元数据或其精确源码来源已澄清。本项目没有修改或合并 Win2D 第三方代码。

Velopack LICENSE 的候选源码路径与打包后路径均为 `licenses/third-party/velopack/1.2.161/LICENSE`。维护者应在依赖版本变化后重新核对相应原文、来源和哈希。
