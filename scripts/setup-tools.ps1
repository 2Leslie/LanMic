[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolRoot = Join-Path $projectRoot '.tools'
$cacheRoot = Join-Path $toolRoot 'downloads'
$extractRoot = Join-Path $toolRoot 'extract'
$lock = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'toolchain.lock.json') | ConvertFrom-Json
New-Item -ItemType Directory -Force -Path $cacheRoot, $extractRoot | Out-Null

function Assert-ToolPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $prefix = $toolRoot.TrimEnd('\') + '\'
    if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a file operation outside the local tool directory: $full"
    }
    return $full
}

function Download-File([string]$Url, [string]$Path) {
    Write-Host "Downloading $Url"
    & curl.exe --fail --location --retry 3 --connect-timeout 30 --output $Path $Url
    if ($LASTEXITCODE -ne 0) { throw "Download failed (curl $LASTEXITCODE): $Url" }
}

function Get-VerifiedArchive([string]$Url, [string]$Name, [string]$Algorithm, [string]$Expected) {
    $path = Join-Path $cacheRoot $Name
    if (Test-Path -LiteralPath $path) {
        if ((Get-FileHash -LiteralPath $path -Algorithm $Algorithm).Hash -ieq $Expected) {
            Write-Host "Using verified cached archive $Name"
            return $path
        }
        Remove-Item -LiteralPath (Assert-ToolPath $path) -Force
    }
    Download-File $Url $path
    $actual = (Get-FileHash -LiteralPath $path -Algorithm $Algorithm).Hash
    if ($actual -ine $Expected) {
        Remove-Item -LiteralPath (Assert-ToolPath $path) -Force
        throw "Checksum mismatch for $Name ($Algorithm). Expected $Expected; received $actual."
    }
    return $path
}

function Install-Archive([string]$Archive, [string]$Destination, [string]$Marker) {
    if (Test-Path -LiteralPath (Join-Path $Destination $Marker)) { return }
    $destinationFull = Assert-ToolPath $Destination
    if (Test-Path -LiteralPath $destinationFull) { throw "Incomplete tool directory already exists: $destinationFull. Move it aside and rerun setup." }
    $temporary = Assert-ToolPath (Join-Path $extractRoot ([Guid]::NewGuid().ToString('N')))
    New-Item -ItemType Directory -Force -Path $temporary | Out-Null
    try {
        if (Get-Command tar.exe -ErrorAction SilentlyContinue) {
            & tar.exe -xf $Archive -C $temporary
            if ($LASTEXITCODE -ne 0) { throw "Extraction failed (tar $LASTEXITCODE): $Archive" }
        } else {
            Expand-Archive -LiteralPath $Archive -DestinationPath $temporary
        }
        $markerName = [IO.Path]::GetFileName($Marker)
        $candidates = @(Get-ChildItem -LiteralPath $temporary -Recurse -File -Filter $markerName)
        $source = $null
        foreach ($candidate in $candidates) {
            $relativeParts = $Marker -split '[\\/]'
            $possible = $candidate.FullName
            foreach ($part in $relativeParts) { $possible = Split-Path -Parent $possible }
            if (Test-Path -LiteralPath (Join-Path $possible $Marker)) { $source = $possible; break }
        }
        if (-not $source) { throw "Archive does not contain expected tool $Marker" }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destinationFull) | Out-Null
        Move-Item -LiteralPath (Assert-ToolPath $source) -Destination $destinationFull
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath (Assert-ToolPath $temporary) -Recurse -Force }
    }
}

$jdkDirectory = Join-Path $toolRoot 'jdk'
if (-not (Test-Path -LiteralPath (Join-Path $jdkDirectory 'bin/javac.exe'))) {
    $jdkArchive = Get-VerifiedArchive $lock.jdk.url 'temurin-jdk21-windows-x64.zip' 'SHA256' $lock.jdk.sha256
    Install-Archive $jdkArchive $jdkDirectory 'bin/javac.exe'
}

$missingPackages = @($lock.android.packages | Where-Object { -not (Test-Path -LiteralPath (Join-Path (Join-Path $toolRoot $_.destination) $_.marker)) })
if ($missingPackages.Count -gt 0) {
    $repositoryFile = Join-Path $cacheRoot 'android-repository.xml'
    Download-File $lock.android.repository $repositoryFile
    [xml]$repository = Get-Content -Raw -LiteralPath $repositoryFile
    foreach ($package in $missingPackages) {
        $remote = @($repository.SelectNodes('//*[local-name()="remotePackage"]') | Where-Object { $_.GetAttribute('path') -eq $package.id })
        if ($remote.Count -ne 1) { throw "Pinned package is absent from official Android metadata: $($package.id)" }
        $archives = @($remote[0].SelectNodes('.//*[local-name()="archive"]') | Where-Object {
            $hostNode = $_.SelectSingleNode('./*[local-name()="host-os"]')
            (-not $hostNode) -or ($hostNode.InnerText -eq 'windows')
        })
        $match = @($archives | Where-Object { $_.SelectSingleNode('./*[local-name()="complete"]/*[local-name()="url"]').InnerText -eq $package.archive })
        if ($match.Count -ne 1) { throw "Pinned Windows archive is absent from official Android metadata: $($package.archive)" }
        $checksum = $match[0].SelectSingleNode('./*[local-name()="complete"]/*[local-name()="checksum"]')
        if (($checksum.GetAttribute('type') -ne 'sha1') -or ($checksum.InnerText -ine $package.sha1)) {
            throw "Official repository checksum changed for $($package.id); inspect metadata and update the lock deliberately."
        }
        $archive = Get-VerifiedArchive ('https://dl.google.com/android/repository/' + $package.archive) $package.archive 'SHA1' $package.sha1
        Install-Archive $archive (Join-Path $toolRoot $package.destination) $package.marker
    }
}

& (Join-Path $jdkDirectory 'bin/java.exe') -version
if ($LASTEXITCODE -ne 0) { throw 'Local Java verification failed.' }
& (Join-Path $toolRoot 'android-sdk/build-tools/35.0.0/aapt2.exe') version
if ($LASTEXITCODE -ne 0) { throw 'Local Android build-tools verification failed.' }
Write-Host 'Workspace-local toolchain is ready. No global PATH, registry, SDK, or Java installation was changed.'
