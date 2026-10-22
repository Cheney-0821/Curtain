param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+-beta\.\d+(?:\.\d+)*$')]
    [string]$Version,
    [switch]$PublishOnly
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repo 'src/Curtain.App/Curtain.App.csproj'
$publish = Join-Path $repo "artifacts/B3/publish-$Version"
$releaseFeed = Join-Path $repo 'artifacts/B3/r2-feed-beta'
$installerArchive = Join-Path $repo "artifacts/B3/installer-$Version"

# A successful build does not prove that a WinUI 3 Publish folder can start.
# The -PublishOnly switch lets us test the actual shipping binaries first.
New-Item -ItemType Directory -Force -Path $publish | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish `
  -p:Platform=x64 -p:Version=$Version -p:WindowsAppSDKSelfContained=true `
  -p:EnableMsixTooling=true -p:PublishReadyToRun=false `
  -p:PublishSingleFile=false -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
$publishedExe = Join-Path $publish 'Curtain.App.exe'
if (-not (Test-Path $publishedExe)) { throw 'Curtain.App.exe missing after publish' }

# Keep the notices beside every published build and inside the Velopack packages.
$projectLicense = Join-Path $repo 'LICENSE'
$licenseSource = Join-Path $repo 'licenses/third-party'
$noticesSource = Join-Path $repo 'docs/THIRD-PARTY-NOTICES.md'
if (-not (Test-Path $projectLicense)) { throw 'Project LICENSE is missing' }
if (-not (Test-Path $licenseSource)) { throw 'Third-party license files are missing' }
if (-not (Test-Path $noticesSource)) { throw 'THIRD-PARTY-NOTICES.md is missing' }
$licenseDestination = Join-Path $publish 'licenses'
if (Test-Path $licenseDestination) {
    Remove-Item -LiteralPath $licenseDestination -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $licenseDestination | Out-Null
Copy-Item -Path (Join-Path $licenseSource '*') -Destination $licenseDestination -Recurse -Force
Copy-Item -LiteralPath $noticesSource -Destination (Join-Path $publish 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item -LiteralPath $projectLicense -Destination (Join-Path $publish 'LICENSE') -Force
Write-Host "Publish output: $publish"
Write-Host 'Project LICENSE, third-party license files and notices copied into publish output.'

if ($PublishOnly) {
    Write-Host 'Publish-only complete. Start the published EXE manually and confirm the main UI opens before packing.'
    return
}

# Only package the publish folder after the direct-publish startup check passes.
New-Item -ItemType Directory -Force -Path $releaseFeed,$installerArchive | Out-Null

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }

dotnet tool run vpk -- pack --packId CurtainPrivacy `
  --packVersion $Version --packTitle Curtain --packDir $publish `
  --mainExe Curtain.App.exe --runtime win-x64 --channel win `
  --icon (Join-Path $repo "src/Curtain.App/Assets/Curtain.ico") `
  --outputDir $releaseFeed
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

Get-ChildItem $releaseFeed -Filter '*Setup.exe' | Copy-Item -Destination $installerArchive -Force
Write-Host "Release feed: $releaseFeed"
Write-Host "Installer archive: $installerArchive"
Write-Host 'This public source candidate does not provide an upload script. Release is handled by the project maintainer using a separate release process.'
