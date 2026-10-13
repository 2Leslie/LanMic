[CmdletBinding()]
param(
    [ValidateSet('All', 'Windows', 'Android')]
    [string]$Target = 'All',
    [switch]$SkipToolSetup
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildRoot = Join-Path $projectRoot 'build'
$distRoot = Join-Path $projectRoot 'dist'
$toolRoot = Join-Path $projectRoot '.tools'
New-Item -ItemType Directory -Force -Path $buildRoot, $distRoot | Out-Null
$builtFiles = @()

function Invoke-Checked([string]$Program, [string[]]$Arguments) {
    # Android's native Windows tools do not consistently accept non-ASCII
    # absolute paths. Run from the workspace and pass relative path arguments.
    # PowerShell still launches the executable through its full Unicode path.
    $prefix = $projectRoot.TrimEnd('\') + '\'
    $nativeArguments = @($Arguments | ForEach-Object {
        if ($_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { '.\' + $_.Substring($prefix.Length) }
        else { $_ }
    })
    Push-Location -LiteralPath $projectRoot
    try {
        & $Program @nativeArguments
        if ($LASTEXITCODE -ne 0) { throw "Build command failed with exit code ${LASTEXITCODE}: $Program" }
    } finally { Pop-Location }
}

function Reset-BuildDirectory([string]$Name) {
    $path = [IO.Path]::GetFullPath((Join-Path $buildRoot $Name))
    $prefix = $buildRoot.TrimEnd('\') + '\'
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe build directory: $path" }
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    return $path
}

if ($Target -in @('All', 'Windows')) {
    Write-Host 'Building Windows receiver...'
    $windowsBuild = Reset-BuildDirectory 'windows'
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
    if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'windows') -Recurse -Filter '*.cs' -File | Sort-Object FullName | ForEach-Object { $_.FullName })
    if ($sources.Count -eq 0) { throw 'No Windows source files found in windows/.' }
    $exe = Join-Path $windowsBuild 'LanMic.exe'
    $arguments = @('/nologo', '/target:winexe', '/platform:anycpu', '/langversion:5', '/codepage:65001', '/optimize+', '/warn:4', ('/out:' + $exe), '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Security.dll')
    $manifest = Join-Path $projectRoot 'windows/app.manifest'
    if (Test-Path -LiteralPath $manifest) { $arguments += '/win32manifest:' + $manifest }
    Invoke-Checked $compiler ($arguments + $sources)
    Copy-Item -LiteralPath $exe -Destination (Join-Path $distRoot 'LanMic.exe') -Force
    $configuration = Join-Path $projectRoot 'windows/App.config'
    if (Test-Path -LiteralPath $configuration) { Copy-Item -LiteralPath $configuration -Destination (Join-Path $distRoot 'LanMic.exe.config') -Force }
    $builtFiles += Join-Path $distRoot 'LanMic.exe'
}

if ($Target -in @('All', 'Android')) {
    if (-not $SkipToolSetup) { & (Join-Path $PSScriptRoot 'setup-tools.ps1') }
    Write-Host 'Building Android sender...'
    $androidBuild = Reset-BuildDirectory 'android'
    $main = Join-Path $projectRoot 'android/app/src/main'
    $manifest = Join-Path $main 'AndroidManifest.xml'
    $resources = Join-Path $main 'res'
    $javaSource = Join-Path $main 'java'
    $javaBin = Join-Path $toolRoot 'jdk/bin'
    $java = Join-Path $javaBin 'java.exe'
    $javac = Join-Path $javaBin 'javac.exe'
    $jar = Join-Path $javaBin 'jar.exe'
    $androidJar = Join-Path $toolRoot 'android-sdk/platforms/android-35/android.jar'
    $buildTools = Join-Path $toolRoot 'android-sdk/build-tools/35.0.0'
    foreach ($required in @($manifest, (Join-Path $javaSource 'io/lanmic/app/MainActivity.java'), (Join-Path $javaSource 'io/lanmic/app/MicService.java'), $java, $javac, $androidJar, (Join-Path $buildTools 'aapt2.exe'))) {
        if (-not (Test-Path -LiteralPath $required)) { throw "Missing build input: $required" }
    }
    $classes = Join-Path $androidBuild 'classes'
    $generated = Join-Path $androidBuild 'generated'
    $dex = Join-Path $androidBuild 'dex'
    New-Item -ItemType Directory -Force -Path $classes, $generated, $dex | Out-Null
    $compiledResources = Join-Path $androidBuild 'resources.zip'
    Invoke-Checked (Join-Path $buildTools 'aapt2.exe') @('compile', '--dir', $resources, '-o', $compiledResources)
    $unsigned = Join-Path $androidBuild 'unsigned.apk'
    Invoke-Checked (Join-Path $buildTools 'aapt2.exe') @('link', '-o', $unsigned, '-I', $androidJar, '--manifest', $manifest, '--java', $generated, '--min-sdk-version', '26', '--target-sdk-version', '35', '--auto-add-overlay', $compiledResources)
    $sources = @(Get-ChildItem -LiteralPath $javaSource, $generated -Recurse -Filter '*.java' -File | Sort-Object FullName | ForEach-Object { $_.FullName })
    if ($sources.Count -eq 0) { throw 'No Java source files found.' }
    $bootClasspath = $androidJar + ';' + (Join-Path $buildTools 'core-lambda-stubs.jar')
    Invoke-Checked $javac (@('-J-Duser.language=en', '-J-Duser.country=US', '-encoding', 'UTF-8', '-source', '8', '-target', '8', '-Xlint:-options', '-bootclasspath', $bootClasspath, '-classpath', $androidJar, '-d', $classes) + $sources)
    $classJar = Join-Path $androidBuild 'classes.jar'
    Invoke-Checked $jar @('cf', $classJar, '-C', $classes, '.')
    Invoke-Checked $java @('-cp', (Join-Path $buildTools 'lib/d8.jar'), 'com.android.tools.r8.D8', '--release', '--min-api', '26', '--lib', $androidJar, '--output', $dex, $classJar)
    $dexFiles = @(Get-ChildItem -LiteralPath $dex -Filter '*.dex' -File | Sort-Object Name)
    if ($dexFiles.Count -eq 0) { throw 'D8 did not produce a dex file.' }
    foreach ($dexFile in $dexFiles) { Invoke-Checked $jar @('uf', $unsigned, '-C', $dex, $dexFile.Name) }
    $aligned = Join-Path $androidBuild 'aligned.apk'
    Invoke-Checked (Join-Path $buildTools 'zipalign.exe') @('-f', '4', $unsigned, $aligned)
    $local = Join-Path $projectRoot '.local'
    New-Item -ItemType Directory -Force -Path $local | Out-Null
    $keyStore = Join-Path $local 'android-debug.keystore'
    if (-not (Test-Path -LiteralPath $keyStore)) {
        Invoke-Checked (Join-Path $javaBin 'keytool.exe') @('-genkeypair', '-noprompt', '-keystore', $keyStore, '-storetype', 'JKS', '-alias', 'androiddebugkey', '-storepass', 'android', '-keypass', 'android', '-keyalg', 'RSA', '-keysize', '2048', '-validity', '10000', '-dname', 'CN=LanMic Development,O=LanMic,C=CN')
    }
    $apk = Join-Path $androidBuild 'LanMic-android.apk'
    $signer = Join-Path $buildTools 'lib/apksigner.jar'
    Invoke-Checked $java @('-jar', $signer, 'sign', '--ks', $keyStore, '--ks-key-alias', 'androiddebugkey', '--ks-pass', 'pass:android', '--key-pass', 'pass:android', '--out', $apk, $aligned)
    Invoke-Checked $java @('-jar', $signer, 'verify', '--verbose', $apk)
    Invoke-Checked (Join-Path $buildTools 'zipalign.exe') @('-c', '4', $apk)
    Invoke-Checked (Join-Path $buildTools 'aapt2.exe') @('dump', 'badging', $apk)
    Copy-Item -LiteralPath $apk -Destination (Join-Path $distRoot 'LanMic-android.apk') -Force
    $builtFiles += Join-Path $distRoot 'LanMic-android.apk'
}

foreach ($file in $builtFiles) {
    $item = Get-Item -LiteralPath $file
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("Built {0} ({1:N0} bytes), SHA-256 {2}" -f $item.FullName, $item.Length, $hash)
}
