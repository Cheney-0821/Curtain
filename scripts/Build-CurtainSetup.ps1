param(
    [Parameter(Mandatory = $true)][string]$PayloadPath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+(?:-beta\.\d+(?:\.\d+)*)?$')][string]$Version,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$FileVersion
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$compiler = Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$payload = (Resolve-Path -LiteralPath $PayloadPath).Path
$target = [IO.Path]::GetFullPath($OutputPath)
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '.NET Framework C# compiler is missing.' }
if (Test-Path -LiteralPath $target) { throw "Refusing to overwrite $target" }
$targetDirectory = Split-Path -Parent $target
New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
$metadata = Join-Path $targetDirectory "setup-version-$([Guid]::NewGuid().ToString('N')).cs"
try {
    @"
using System.Reflection;
[assembly: AssemblyTitle("Curtain Setup")]
[assembly: AssemblyProduct("Curtain")]
[assembly: AssemblyCompany("Curtain")]
[assembly: AssemblyVersion("$FileVersion")]
[assembly: AssemblyFileVersion("$FileVersion")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content -LiteralPath $metadata -Encoding UTF8
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $repo 'src/Curtain.Setup') -File -Filter '*.cs' | ForEach-Object FullName)
    $icon = Join-Path $repo 'src/Curtain.App/Assets/Curtain.ico'
    $compilerArguments = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/codepage:65001',
        '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Xml.dll',
        '/reference:System.Xml.Linq.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll', '/reference:Microsoft.CSharp.dll',
        "/win32icon:$icon", "/resource:$payload,Curtain.Setup.Velopack", "/out:$target", $metadata) + $sources
    & $compiler @compilerArguments
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $target -PathType Leaf)) { throw 'Curtain Setup build failed.' }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($target)
    if ($info.ProductVersion -ne $Version -or $info.FileVersion -ne $FileVersion) { throw 'Curtain Setup version mismatch.' }
    Write-Host "User installer: $target"
} finally {
    if (Test-Path -LiteralPath $metadata) { Remove-Item -LiteralPath $metadata }
}
