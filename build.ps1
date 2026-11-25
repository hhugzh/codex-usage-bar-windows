$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$src = Join-Path $root 'src'
$marketplace = Join-Path $root 'marketplace'
$out = Join-Path $root 'out'
$version = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.]+)?$') { throw 'Invalid version.' }

$framework = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319')
) | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'csc.exe') -PathType Leaf } | Select-Object -First 1
if (!$framework) { throw 'The Windows .NET Framework C# compiler was not found.' }
$wpf = Join-Path $framework 'WPF'
$compiler = Join-Path $framework 'csc.exe'
$logReader = Join-Path $src 'codex-usage-bar.cs'
$window = Join-Path $src 'codex-usage-window.cs'
$bridge = Join-Path $src 'codex-usage-bridge.cs'
$xaml = Join-Path $src 'codex-usage-bar.xaml'
$manifest = Join-Path $src 'codex-usage-bar.manifest'

New-Item -ItemType Directory -Path $out -Force | Out-Null
$exe = Join-Path $out 'CodexUsageBar.exe'
$appArgs = @(
    '/nologo', '/target:winexe', '/optimize+', '/codepage:65001',
    "/win32manifest:$manifest", "/resource:$xaml,UsageBar.xaml",
    "/r:$wpf\WindowsBase.dll", "/r:$wpf\PresentationCore.dll", "/r:$wpf\PresentationFramework.dll",
    "/r:$framework\System.Xaml.dll", '/r:System.Web.Extensions.dll', "/out:$exe", $logReader, $window
)
& $compiler @appArgs
if ($LASTEXITCODE -ne 0) { throw 'Could not compile the usage bar.' }

$launcherBin = Join-Path $marketplace 'plugins\codex-usage\bin'
$reportBin = Join-Path $marketplace 'plugins\codex-usage-report\bin'
New-Item -ItemType Directory -Path $launcherBin,$reportBin -Force | Out-Null
$launcher = Join-Path $launcherBin 'CodexUsageBridge.exe'
& $compiler /nologo /target:winexe /optimize+ /codepage:65001 /r:System.Web.Extensions.dll "/out:$launcher" $logReader $bridge
if ($LASTEXITCODE -ne 0) { throw 'Could not compile the plugin bridge.' }
Copy-Item -LiteralPath $launcher -Destination (Join-Path $reportBin 'CodexUsageBridge.exe') -Force

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $out "codex-usage-bar-windows-$version.zip"
$pending = "$zip.$PID.tmp"
$files = [ordered]@{ 'CodexUsageBar.exe' = $exe }
foreach ($name in @('README.md','CHANGELOG.md','VERSION','install-companion.ps1')) {
    $files[$name] = Join-Path $root $name
}
foreach ($name in @('.agents/plugins/marketplace.json',
    'plugins/codex-usage/plugin.json', 'plugins/codex-usage/mcp.json', 'plugins/codex-usage/bin/CodexUsageBridge.exe',
    'plugins/codex-usage-report/plugin.json', 'plugins/codex-usage-report/mcp.json', 'plugins/codex-usage-report/bin/CodexUsageBridge.exe')) {
    $files["marketplace/$name"] = Join-Path $marketplace $name
}

try {
    $archive = [IO.Compression.ZipFile]::Open($pending, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entry in $files.GetEnumerator()) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive, $entry.Value, $entry.Key, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally {
        $archive.Dispose()
    }
    Move-Item -LiteralPath $pending -Destination $zip -Force
} finally {
    if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending -Force }
}

Write-Output "Release ZIP: $zip"
Get-FileHash -LiteralPath $zip -Algorithm SHA256
