param(
    [Parameter(Mandatory=$true)][int]$ParentProcessId,
    [Parameter(Mandatory=$true)][long]$ParentStartTicks,
    [Parameter(Mandatory=$true)][ValidatePattern('^Curtain-Diagnostics-[0-9A-F]{16}-[0-9]+$')][string]$SessionName,
    [Parameter(Mandatory=$true)][string]$StatePath,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{8}$')][string]$RunToken,
    [ValidateRange(1,86520)][int]$TimeoutSeconds=86520
)
$ErrorActionPreference='Stop'
if ([IO.Path]::GetFileName($StatePath) -ne ($SessionName + '.json')) { exit 2 }
$parent=Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
if ($null -ne $parent -and $parent.StartTime.ToUniversalTime().Ticks -eq $ParentStartTicks) {
    try { Wait-Process -InputObject $parent -Timeout $TimeoutSeconds -ErrorAction Stop } catch { }
}
$guardPath=[IO.Path]::ChangeExtension($StatePath,'.guard')
$guard=$null
$deadline=[DateTime]::UtcNow.AddSeconds(20)
while ($null -eq $guard -and [DateTime]::UtcNow -lt $deadline) {
    try { $guard=[IO.File]::Open($guardPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None) }
    catch [IO.IOException] { Start-Sleep -Milliseconds 100 }
}
if ($null -eq $guard) { exit 3 }
try {
    $owner=Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    if ($owner.SessionName -ne $SessionName -or $owner.RunToken -ne $RunToken -or $owner.ParentProcessId -ne $ParentProcessId) { exit 0 }
    $logman=Join-Path $env:windir 'System32/logman.exe'
    & $logman stop $SessionName -ets *> $null
    $stopCode=$LASTEXITCODE
    $owner | Add-Member -NotePropertyName WatchdogExitCode -NotePropertyValue $stopCode -Force
    $owner | ConvertTo-Json -Compress | Set-Content -LiteralPath $StatePath -Encoding UTF8
    exit $stopCode
} finally { $guard.Dispose() }
