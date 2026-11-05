param([string]$SourceDirectory = (Join-Path $PSScriptRoot '..\src\Curtain.App'))
$ErrorActionPreference = 'Stop'

function Assert-ExpanderItems([xml]$Document, [string]$Label) {
    $groups = @($Document.SelectNodes("//*[local-name()='SettingsExpander.Items' or local-name()='ScrollAwareSettingsExpander.Items']"))
    foreach ($group in $groups) {
        foreach ($item in $group.ChildNodes) {
            if ($item -is [System.Xml.XmlElement] -and
                ($item.LocalName -ne 'SettingsCard' -or $item.NamespaceURI -ne 'using:CommunityToolkit.WinUI.Controls')) {
                throw "Invalid SettingsExpander item in ${Label}: $($item.LocalName). Default container styles require SettingsCard."
            }
        }
    }
    return $groups.Count
}

# The R1 structure must be rejected before checking the current source.
$bad = [xml]'<Root xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:c="using:Curtain.App.Controls"><c:ScrollAwareSettingsExpander.Items><StackPanel /></c:ScrollAwareSettingsExpander.Items></Root>'
$rejected = $false
try { Assert-ExpanderItems $bad 'R1 regression fixture' | Out-Null } catch { $rejected = $true }
if (!$rejected) { throw 'Regression fixture was not rejected.' }
Write-Output 'PASS: invalid R1 item type rejected'

$count = 0
foreach ($file in Get-ChildItem -LiteralPath $SourceDirectory -Filter '*.xaml' -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }) {
    [xml]$document = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    $count += Assert-ExpanderItems $document $file.Name
}
if ($count -eq 0) { throw 'No SettingsExpander groups found.' }
Write-Output "PASS: $count SettingsExpander item groups use supported card containers"
