#Requires -Version 7.0
<#
Synthetic end-to-end smoke test, never sends actual user diagnostics.
Run health preflight first. Add -Upload only after the target Worker advertises multipart-v1.
#>
param(
    [string]$BaseUrl = 'https://feedback.cheneylab.cn',
    [switch]$Upload,
    [string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
$base = $BaseUrl.TrimEnd('/')
$health = Invoke-RestMethod -Method Get -Uri "$base/health" -TimeoutSec 15
Write-Host "Health: $($health.status), protocol: $($health.protocol)"
if ($health.status -ne 'ok') { throw 'Worker health check not OK' }
if ($health.protocol -ne 'multipart-v1' -or $health.service -ne 'curtain-feedback-upload') {
    throw 'Server protocol differs from candidate multipart-v1. Do not upload until both sides are reconciled.'
}
if (-not $Upload) { Write-Host 'Health preflight complete. Upload test not requested.'; return }
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('curtain-feedback-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $readme = Join-Path $temp 'README.txt'
    [System.IO.File]::WriteAllText($readme, 'Synthetic Curtain diagnostic test. No personal data.', [System.Text.Encoding]::UTF8)
    $zip = Join-Path $temp 'Curtain-Diagnostic-Synthetic.zip'
    Compress-Archive -Path $readme -DestinationPath $zip -CompressionLevel Optimal
    $id = [guid]::NewGuid().ToString('D')
    $receipts = @()
    foreach ($attempt in 1..2) {
    $raw = @(& curl.exe -sS --fail-with-body --max-time 60 -w "`n%{http_code}" -H 'X-Curtain-Consent: 1' `
        -F "file=@$zip;type=application/zip" `
        -F 'email=' `
        -F 'description=synthetic smoke test - no personal data' `
        -F 'version=0.2.0-beta.6' `
        -F "requestId=$id" `
        "$base/api/feedback")
    if ($LASTEXITCODE -ne 0) { throw "HTTP upload failed (curl exit $LASTEXITCODE)" }
    if ($raw[-1] -ne '201') { throw 'Expected HTTP 201' }
    $response = ($raw[0..($raw.Count - 2)] -join "`n") | ConvertFrom-Json
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($response.feedbackId -ne $id -or $response.retentionDays -ne 14 -or
        $response.sizeBytes -ne (Get-Item -LiteralPath $zip).Length -or $response.sha256 -ne $hash) {
        throw 'Unexpected feedback receipt; inspect endpoint and Worker configuration.'
    }
    $receipts += $response
    }
    if ($receipts[0].receivedAt -ne $receipts[1].receivedAt) { throw 'Retry receipt changed' }
    if ($EvidenceDirectory) {
        New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
        @{ synthetic = $true; attempts = 2; httpStatus = 201; receipts = $receipts } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'synthetic-receipts.json') -Encoding utf8
    }
    Write-Host "UPLOAD_PASS feedbackId=$id bytes=$($response.sizeBytes) retentionDays=$($response.retentionDays)"
    Write-Host "Inspect private R2 key: incoming/$id.zip, then delete this synthetic test object."
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
