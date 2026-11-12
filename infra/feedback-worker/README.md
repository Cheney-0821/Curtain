# Curtain 诊断反馈 Worker

Cloudflare Worker，用于接收用户主动提交的 Curtain 诊断 ZIP。服务没有公开读取或下载接口；诊断文件应存入私有 R2 bucket，并按部署者配置的保留策略自动清理。

## HTTP 接口

- `GET /health` 返回服务状态及 `multipart-v1` 协议标识。
- `POST /api/feedback` 接受 multipart 表单，必须带 `X-Curtain-Consent: 1`。
- 必填字段：`file`（ZIP）、`version`、`requestId`；可选字段：`email`、`description`。
- ZIP 上限 16 MiB；描述上限 300 个字符；成功时返回反馈编号、接收时间、大小、SHA-256 和 14 天保留期。
- 相同 request ID 与相同文件可安全重试；同 ID 不同内容会返回冲突。

## 数据处理

Worker 不解压诊断包，只做 ZIP 结构检查。上传对象存入私有 `incoming/` 路径，没有公开读取接口。请求需要用户明确同意；邮箱和描述均为选填。日志只写固定的路由、状态和原因码，不记录 IP、请求头、文件名、邮箱、描述或文件内容。

诊断包可能包含系统和应用运行信息。运营方应在部署前核对诊断包内容、私有存储权限、保留期限、访问控制和当地隐私要求。当前 Worker 对上传大小和数量使用软预算，不能作为严格的存储配额或恶意软件扫描。

## 本地检查

使用仓库锁定的 Wrangler 版本和 lockfile：

```powershell
npm ci
npm test
npx wrangler deploy --dry-run --config wrangler.local.jsonc
pwsh -File scripts/Test-Feedback.ps1
```

`Test-Feedback.ps1` 默认只检查 `/health`。只有在目标服务确认返回 `multipart-v1` 后，才可显式使用 `-Upload` 发送无个人数据的合成 ZIP；上传后须由有权限的维护者核对并删除合成对象。

## 部署

部署者需要自行配置 Worker 路由、私有 R2 bucket、两个限流绑定及 14 天对象生命周期。`wrangler.jsonc` 是配置示例，须先核对 Cloudflare 账户中的绑定和目标，再部署。不要提交 API token、密钥、用户诊断包或账户凭据。

公开配置中的 YOUR_ 占位符须替换为部署者自己的资源绑定。将配置复制为 wrangler.local.jsonc，填写私有 R2 bucket 和数字限流命名空间。部署时使用 npx wrangler deploy --config wrangler.local.jsonc。
