# 诊断版本与会话归属（beta.7）

## 版本合同

产品版本采用完整 SemVer。当前 0.2.0-beta.7 → FileVersion 0.2.0.7，AssemblyVersion 在 0.2 兼容系列内保持 0.2.0.0。数字文件版本的唯一来源为 release/diagnostic-version-map.json；它作为资源嵌入程序集并随发布文件携带。新增发布版本必须新增唯一数字键和唯一 SemVer 值，禁止复用已登记数字。B3-Pack 显式传递并验证 EXE/DLL 的 FileVersion、ProductVersion 及 DLL AssemblyVersion。表不含旧 0.2.0.0，因为它对应多个历史产品版本；该值永远为 LegacyAmbiguous。未登记值为 Unknown，禁止根据当前安装版本推断。

每次构建生成随机 Build ID 和 UTC 时间，记录完整 Git HEAD 与工作树状态（true / false / Unknown 三态字符串）。本地候选如实为 dirty，Git HEAD 只表示构建基点，不代表未提交内容属于该公开提交。候选内测重建可沿用同一产品版本，由 Build ID 区分；公开发布必须登记新产品版本。缺失构建元数据保留 Unknown。

## Schema 2

诊断摘要、environment、manifest 均包含相同 versionIdentity 和 appSession 对象，使用 camelCase。摘要额外保留 productVersion/fileVersion 及兼容 applicationVersion；环境保留 productVersion/fileVersion/assemblyVersion。manifest.files 继续记录每个文件大小及 SHA-256（manifest 自身除外）。摘要明确 JSONL 24 小时、Windows Application 事件 7 天的采集窗口。

每条新 JSONL 自动补齐 productVersion、fileVersion、gitCommit、buildId、appSessionId、appSessionStartedAtUtc。applicationProcessId 为当前应用进程；进程会话 GUID 每次进程启动生成，开始时间取操作系统进程创建时间。Initialize 重入不刷新会话。原 sessionId 仍用于业务保护会话，不能被替换。旧 JSONL 缺失这些字段仍可脱敏打包，不补写当前版本到历史行。ETW DiagnosticRecord（ID 1，Version 2）采用相同语义，ETW XML 导出白名单同步增加这些字段。

## Windows 崩溃事件

System/Execution ProcessID 是事件生成进程，不是故障 PID。输出明确命名 eventWriterProcessId。仅 Application Error/1000 的命名 ProcessId 或 FaultingProcessId 作为可靠故障 PID，可解析十进制或 0x 十六进制。无可靠字段不关联当前进程。

时间早于当前进程创建时间一律 BeforeCurrentSessionStart/HistoricalEvent；此后也须故障 PID、时间与明确映射的当前产品版本一致才 Matched。PID 不同或历史事件为 OtherProcess；证据不足为 Unknown/NotAvailable。versionAttribution 独立为 Exact/LegacyAmbiguous/Unknown，resolvedProductVersion 仅 Exact 时存在。未读取本地事件的路径、完整异常文本或账户信息。

## 安装识别

优先调用 GetCurrentPackageFullName 原生包身份检查：ERROR_INSUFFICIENT_BUFFER（122）表示有包身份，直接返回 MsixPackaged；APPMODEL_ERROR_NO_PACKAGE（15700）才允许继续探测 Velopack。其他返回码必须返回 Unknown，不得当作“未打包”。MSIX 分支在初始化 Velopack UpdateManager 之前短路，避免 Store 构建误触发 Direct 更新管理器。只有确认无包身份后，才用 Velopack 1.2.161 UpdateManager.IsInstalled 且非 IsPortable 判断真实安装；没有执行检查更新/下载请求。Velopack 检测异常返回 Unknown 并仅记录异常类型。否则采用构建期明确的 DistributionKind（Development / PortableOrUnpacked），无证据 Unknown。输出 installKindDetectionMethod，不输出安装目录。MSIX 仅检测分支，没有增加 Store 发行实现。DllImport 使用 CharSet.Unicode 与 ExactSpelling=true。工作树状态统一为 true / false / Unknown 三态，测试不得将某次 dirty 候选状态写死。

## 官方依据

- https://learn.microsoft.com/en-us/windows/msix/detect-package-identity

- Microsoft Execution 元素：https://learn.microsoft.com/en-us/windows/win32/wes/eventschema-execution-systempropertiestype-element
- Velopack IsInstalled：https://docs.velopack.io/reference/cs/Velopack/UpdateManager/properties/IsInstalled

原始 ETL 仍遵循原有用户选项，可能带设备/用户路径；结构化及 XML 导出继续执行白名单。