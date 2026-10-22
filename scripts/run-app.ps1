#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    dotnet build '.\src\Curtain.App\Curtain.App.csproj' -c Debug -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'App build failed' }
    $app = Get-ChildItem -Path '.\src\Curtain.App\bin' -Filter 'Curtain.App.exe' -File -Recurse |
        Where-Object { $_.FullName -match 'Debug' -and $_.FullName -match 'win-x64' } |
        Select-Object -First 1
    if (-not $app) { throw 'Curtain.App.exe not found after build' }
    Start-Process -FilePath $app.FullName
} finally { Pop-Location }
