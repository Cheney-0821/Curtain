#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    Write-Host '=== Curtain Windows toolchain ==='
    dotnet --info
    if ($LASTEXITCODE -ne 0) { throw 'dotnet --info failed' }
    Write-Host '=== Restore app ==='
    dotnet restore '.\src\Curtain.App\Curtain.App.csproj' -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw 'restore app failed' }
    Write-Host '=== Debug build app ==='
    dotnet build '.\src\Curtain.App\Curtain.App.csproj' -c Debug -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Debug build app failed' }
    Write-Host '=== Core assertions ==='
    dotnet run --project '.\tests\Curtain.Core.Tests\Curtain.Core.Tests.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Core assertions failed' }
    Write-Host '=== Release build app ==='
    dotnet build '.\src\Curtain.App\Curtain.App.csproj' -c Release -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Release build app failed' }
    Write-Host '=== Release build Windows fixture ==='
    dotnet build '.\tests\Curtain.Windows.Fixture\Curtain.Windows.Fixture.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Release build Windows fixture failed' }
    Write-Host 'PASS: restore, Debug/Release x64 app builds, Core assertions, and Release Windows fixture build. Interactive Windows validation is tracked separately.'
} finally {
    Pop-Location
}
