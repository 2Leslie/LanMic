[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$distRoot = Join-Path $projectRoot 'dist'
$packageRoot = Join-Path $projectRoot 'build/package/LanMic-Windows'
New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
foreach ($name in @('LanMic.exe', 'LanMic-android.apk')) {
    if (-not (Test-Path -LiteralPath (Join-Path $distRoot $name))) { throw "Build both apps first. Missing $name" }
}
Copy-Item -LiteralPath (Join-Path $distRoot 'LanMic.exe') -Destination $packageRoot -Force
$config = Join-Path $distRoot 'LanMic.exe.config'
if (Test-Path -LiteralPath $config) { Copy-Item -LiteralPath $config -Destination $packageRoot -Force }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $packageRoot 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/VALIDATION.md') -Destination (Join-Path $packageRoot 'VALIDATION.md') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $packageRoot 'images') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/images/windows.png') -Destination (Join-Path $packageRoot 'images/windows.png') -Force
$zip = Join-Path $distRoot 'LanMic-Windows.zip'
Compress-Archive -LiteralPath $packageRoot -DestinationPath $zip -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination (Join-Path $distRoot 'README.md') -Force
$lines = foreach ($name in @('LanMic-Windows.zip', 'LanMic-android.apk', 'LanMic.exe')) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $distRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $name"
}
[IO.File]::WriteAllLines((Join-Path $distRoot 'SHA256SUMS.txt'), [string[]]$lines, [Text.UTF8Encoding]::new($false))
Write-Host 'Release assets prepared in dist/: LanMic-Windows.zip, LanMic-android.apk, SHA256SUMS.txt'
