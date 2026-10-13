[CmdletBinding()]
param([switch]$SkipAudioOutput)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $projectRoot 'build/tests'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$exe = Join-Path $projectRoot 'dist/LanMic.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build the Windows app first with scripts/build.ps1 -Target Windows.' }
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
Copy-Item -LiteralPath $exe -Destination (Join-Path $testRoot 'LanMic.exe') -Force
$config = Join-Path $projectRoot 'dist/LanMic.exe.config'
function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Test command failed (exit $LASTEXITCODE): $Program" }
}
function Build-Test([string]$Name) {
    $target = Join-Path $testRoot ($Name + '.exe')
    Invoke-Checked $compiler @('/nologo', '/target:exe', '/langversion:5', '/codepage:65001', ('/out:' + $target), ('/reference:' + $exe), (Join-Path $projectRoot ('tests/' + $Name + '.cs')))
    if (Test-Path -LiteralPath $config) { Copy-Item -LiteralPath $config -Destination ($target + '.config') -Force }
    return $target
}
Write-Host 'Running real TLS receiver integration tests...'
$receiverTest = Build-Test 'ReceiverIntegrationTests'
Invoke-Checked $receiverTest @()

$javaBin = Join-Path $projectRoot '.tools/jdk/bin'
$classes = Join-Path $testRoot 'java'
New-Item -ItemType Directory -Force -Path $classes | Out-Null
Write-Host 'Compiling the production Android network client for cross-language testing...'
Invoke-Checked (Join-Path $javaBin 'javac.exe') @('-encoding', 'UTF-8', '--release', '8', '-Xlint:-options', '-d', $classes, (Join-Path $projectRoot 'android/app/src/main/java/io/lanmic/app/MicConnection.java'), (Join-Path $projectRoot 'tests/JavaWireIntegration.java'))
$javaTest = Build-Test 'JavaBridgeTests'
Invoke-Checked $javaTest @((Join-Path $javaBin 'java.exe'), $classes)

if (-not $SkipAudioOutput) {
    Write-Host 'Checking actual Windows audio output with silence only...'
    $audioTest = Build-Test 'AudioOutputSmokeTests'
    Invoke-Checked $audioTest @()
}
Write-Host 'All requested checks passed. A real Android phone is still needed for microphone/background and end-to-end validation.'
