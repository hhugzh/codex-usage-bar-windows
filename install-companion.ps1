$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'CodexUsageBar.exe'
if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
    $source = Join-Path $PSScriptRoot 'out\CodexUsageBar.exe'
}
if (!(Test-Path -LiteralPath $source -PathType Leaf)) {
    throw 'CodexUsageBar.exe is missing. Extract the release ZIP or run build.ps1 first.'
}

$installDir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CodexUsageBar'
$exePath = Join-Path $installDir 'CodexUsageBar.exe'
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Codex Usage Bar.lnk'
New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $exePath -Force

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exePath
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'Open the Codex usage bar.'
$shortcut.Save()
Write-Output "Installed: $exePath"
Write-Output "Start menu: $shortcutPath"
