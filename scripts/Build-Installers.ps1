#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes the app and builds WiX online MSIs using only the .NET SDK.

.PARAMETER Installer
  Online (downloads models after install). The offline installer is currently not built by this helper.

.PARAMETER RuntimeIdentifier
  Windows runtime identifier(s) to build. Use win-x64, win-arm64, or all.

.PARAMETER SkipPublish
  Reuse existing artifacts\<rid>\publish directories without running dotnet publish.

.PARAMETER SkipChocolatey
  Build MSI artifacts only.

.PARAMETER ChocolateyOnly
  Build the Chocolatey package from existing artifacts\installer MSIs.

.NOTES
  Requires .NET 8 SDK. WiX Toolset is restored via NuGet (WixToolset.Sdk); no separate WiX install needed.
#>
param(
    [ValidateSet("Online")]
    [string] $Installer = "Online",

    [ValidateSet("win-x64", "win-arm64", "all")]
    [string[]] $RuntimeIdentifier = @("all"),

    [switch] $SkipPublish,
    [switch] $SkipChocolatey,
    [switch] $ChocolateyOnly,
    [string] $PackageVersion,
    [string] $AssemblyVersion,
    [string] $FileVersion,
    [string] $InformationalVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($SkipChocolatey -and $ChocolateyOnly) {
    throw "-SkipChocolatey and -ChocolateyOnly cannot be used together."
}

function Get-RepoVersion {
    param([string] $RepoRoot)
    [xml] $doc = Get-Content (Join-Path $RepoRoot "Directory.Build.props")
    $pg = @($doc.Project.PropertyGroup) | Where-Object { $_.Version } | Select-Object -First 1
    if (-not $pg -or -not $pg.Version) {
        throw "Could not read Version from Directory.Build.props"
    }
    return $pg.Version.Trim()
}

function Get-MsiCompatibleVersion {
    param([string] $Version)

    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Version is required to compute an MSI-compatible package version."
    }

    $normalizedVersion = $Version.Trim()
    $match = [regex]::Match($normalizedVersion, '^(?<core>\d+\.\d+\.\d+)')
    if (-not $match.Success) {
        throw "Version '$Version' does not start with a semver core like 1.2.3."
    }

    return $match.Groups['core'].Value
}

function Get-ChocoCompatibleVersion {
    param([string] $Version)

    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "Version is required to compute a Chocolatey package version."
    }

    $normalizedVersion = $Version.Trim()
    if ($normalizedVersion -match '^\d+\.\d+\.\d+(-[0-9A-Za-z][0-9A-Za-z\.-]*)?$') {
        return $normalizedVersion
    }

    return Get-MsiCompatibleVersion -Version $normalizedVersion
}

function Resolve-RuntimeIdentifiers {
    param([string[]] $RequestedRuntimeIdentifiers)

    if ($RequestedRuntimeIdentifiers -contains "all") {
        return @("win-x64", "win-arm64")
    }

    return @($RequestedRuntimeIdentifiers | Select-Object -Unique)
}

function Get-InstallerPlatform {
    param([string] $Rid)

    switch ($Rid) {
        "win-x64" { return "x64" }
        "win-arm64" { return "arm64" }
        default { throw "Unsupported runtime identifier '$Rid'." }
    }
}

function Test-SelfContainedPublishOutput {
    param([string] $PublishDir)

    $requiredFiles = @(
        "PrimeDictate.exe",
        "PrimeDictate.dll",
        "PrimeDictate.runtimeconfig.json",
        "hostfxr.dll",
        "hostpolicy.dll",
        "coreclr.dll",
        "System.Private.CoreLib.dll",
        "PresentationFramework.dll",
        "WindowsBase.dll"
    )

    foreach ($file in $requiredFiles) {
        $path = Join-Path $PublishDir $file
        if (-not (Test-Path $path)) {
            throw "Publish output is missing required self-contained runtime file: $path"
        }
    }

    $runtimeConfigPath = Join-Path $PublishDir "PrimeDictate.runtimeconfig.json"
    $runtimeConfig = Get-Content -Raw $runtimeConfigPath
    if ($runtimeConfig -notmatch '"includedFrameworks"') {
        throw "Publish output is framework-dependent. Expected includedFrameworks in $runtimeConfigPath"
    }
}

function Test-MsiContainsSelfContainedRuntime {
    param([string] $MsiPath)

    $requiredFiles = @(
        "hostfxr.dll",
        "hostpolicy.dll",
        "coreclr.dll",
        "System.Private.CoreLib.dll",
        "PresentationFramework.dll",
        "WindowsBase.dll"
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $view = $null
    try {
        $database = $installer.OpenDatabase($MsiPath, 0)
        $view = $database.OpenView('SELECT `FileName` FROM `File`')
        $view.Execute()

        $fileNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        while ($record = $view.Fetch()) {
            $fileName = $record.StringData(1)
            if ($fileName.Contains("|")) {
                $fileName = $fileName.Split("|")[-1]
            }

            [void] $fileNames.Add($fileName)
        }

        foreach ($file in $requiredFiles) {
            if (-not $fileNames.Contains($file)) {
                throw "MSI payload is missing required self-contained runtime file: $file ($MsiPath)"
            }
        }
    }
    finally {
        if ($view -ne $null) {
            [void] [System.Runtime.InteropServices.Marshal]::ReleaseComObject($view)
        }

        if ($database -ne $null) {
            [void] [System.Runtime.InteropServices.Marshal]::ReleaseComObject($database)
        }

        [void] [System.Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$onlineProj = Join-Path $repoRoot "installer\wix\online\PrimeDictate.Online.wixproj"
$outDir = Join-Path $repoRoot "artifacts\installer"
$version = if ([string]::IsNullOrWhiteSpace($PackageVersion)) { Get-RepoVersion -RepoRoot $repoRoot } else { $PackageVersion }
$msiVersion = Get-MsiCompatibleVersion -Version $version
$chocoVersion = Get-ChocoCompatibleVersion -Version $version
$requestedRids = Resolve-RuntimeIdentifiers -RequestedRuntimeIdentifiers $RuntimeIdentifier
$msbuildProps = @()
if (-not [string]::IsNullOrWhiteSpace($msiVersion)) {
    $msbuildProps += "-p:Version=$msiVersion"
}
if (-not [string]::IsNullOrWhiteSpace($AssemblyVersion)) {
    $msbuildProps += "-p:AssemblyVersion=$AssemblyVersion"
}
if (-not [string]::IsNullOrWhiteSpace($FileVersion)) {
    $msbuildProps += "-p:FileVersion=$FileVersion"
}
if (-not [string]::IsNullOrWhiteSpace($InformationalVersion)) {
    $msbuildProps += "-p:InformationalVersion=$InformationalVersion"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if (-not $ChocolateyOnly) {
    foreach ($rid in $requestedRids) {
        $installerPlatform = Get-InstallerPlatform -Rid $rid
        $publishDir = Join-Path $repoRoot (Join-Path "artifacts" (Join-Path $rid "publish"))

        if (-not $SkipPublish) {
            & (Join-Path $PSScriptRoot "Publish-Windows.ps1") `
                -RuntimeIdentifier $rid `
                -PackageVersion $PackageVersion `
                -AssemblyVersion $AssemblyVersion `
                -FileVersion $FileVersion `
                -InformationalVersion $InformationalVersion
        }

        if (-not (Test-Path (Join-Path $publishDir "PrimeDictate.exe"))) {
            throw "Publish output missing PrimeDictate.exe at $publishDir. Run without -SkipPublish."
        }

        Test-SelfContainedPublishOutput -PublishDir $publishDir
        $publishDirFull = (Resolve-Path $publishDir).Path

        Write-Host "Building online MSI for $rid..."
        dotnet build $onlineProj `
            -c Release `
            "-p:RuntimeIdentifier=$rid" `
            "-p:InstallerPlatform=$installerPlatform" `
            "-p:PublishDir=$publishDirFull" `
            $msbuildProps
        if ($LASTEXITCODE -ne 0) {
            throw "Online WiX build failed for $rid with exit code $LASTEXITCODE"
        }

        $builtOnlineMsi = Join-Path $repoRoot "installer\wix\online\bin\Release\PrimeDictate-$msiVersion-Windows-$installerPlatform-Online.msi"
        $publishedOnlineMsi = Join-Path $outDir "PrimeDictate-$version-Windows-$installerPlatform-Online.msi"
        if (-not (Test-Path $builtOnlineMsi)) {
            throw "Expected built MSI not found: $builtOnlineMsi"
        }

        Test-MsiContainsSelfContainedRuntime -MsiPath $builtOnlineMsi
        Copy-Item -Force $builtOnlineMsi $publishedOnlineMsi
    }
}

if ($SkipChocolatey) {
    Write-Host "Skipping Chocolatey package."
    Write-Host "Done. Artifacts available in: $outDir"
    return
}

Write-Host "Building Chocolatey package..."
$chocoDir = Join-Path $repoRoot "installer\chocolatey"
$chocoToolsDir = Join-Path $chocoDir "tools"
if (-not (Test-Path $chocoToolsDir)) {
    New-Item -ItemType Directory -Force -Path $chocoToolsDir | Out-Null
}

$x64Msi = Join-Path $outDir "PrimeDictate-$version-Windows-x64-Online.msi"
$arm64Msi = Join-Path $outDir "PrimeDictate-$version-Windows-arm64-Online.msi"

if ((Test-Path $x64Msi) -and (Test-Path $arm64Msi)) {
    Get-ChildItem -Path $chocoToolsDir -Filter "PrimeDictate-Online*.msi" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force

    $installScriptPath = Join-Path $chocoToolsDir "chocolateyInstall.ps1"
    $verificationPath = Join-Path $chocoToolsDir "VERIFICATION.txt"
    $originalInstallScript = if (Test-Path $installScriptPath) { Get-Content -Raw $installScriptPath } else { throw "Missing Chocolatey install script: $installScriptPath" }
    $originalVerification = if (Test-Path $verificationPath) { Get-Content -Raw $verificationPath } else { $null }

    $x64Hash = (Get-FileHash -Path $x64Msi -Algorithm SHA256).Hash.ToLowerInvariant()
    $arm64Hash = (Get-FileHash -Path $arm64Msi -Algorithm SHA256).Hash.ToLowerInvariant()
    $stampedInstallScript = $originalInstallScript.Replace("__PRIMEDICTATE_X64_SHA256__", $x64Hash).Replace("__PRIMEDICTATE_ARM64_SHA256__", $arm64Hash)
    $verification = @"
VERIFICATION
Verification is intended to assist package reviewers and future maintainers.

1. Download the official release installers from:
   https://github.com/CakeRepository/PrimeDictate/releases/download/v$chocoVersion/PrimeDictate-Setup-v$chocoVersion-x64.msi
   https://github.com/CakeRepository/PrimeDictate/releases/download/v$chocoVersion/PrimeDictate-Setup-v$chocoVersion-arm64.msi

2. Compute SHA256 checksums of the downloaded MSIs.
   Expected x64 SHA256:
   $x64Hash

   Expected arm64 SHA256:
   $arm64Hash

3. Compare the computed checksums with the checksum values embedded in:
   tools\chocolateyInstall.ps1

4. This package is built from source and release automation in:
   https://github.com/CakeRepository/PrimeDictate

Notes:
- The Chocolatey package downloads release MSIs from GitHub instead of bundling them.
- chocolateyInstall.ps1 selects the native ARM64 URL on ARM64 Windows and the x64 URL otherwise.
- Chocolatey verifies each downloaded MSI with SHA256 before installing it.
"@
    Set-Content -Path $installScriptPath -Value $stampedInstallScript -Encoding UTF8 -NoNewline
    Set-Content -Path $verificationPath -Value $verification -Encoding UTF8 -NoNewline

    try {
        if (Get-Command choco -ErrorAction SilentlyContinue) {
            $originalLocation = Get-Location
            try {
                Set-Location $chocoDir
                choco pack "primedictate.nuspec" --version $chocoVersion
                if ($LASTEXITCODE -ne 0) {
                    Write-Warning "Chocolatey pack failed with exit code $LASTEXITCODE"
                } else {
                    $nupkgFile = Join-Path $chocoDir "primedictate.$chocoVersion.nupkg"
                    if (Test-Path $nupkgFile) {
                        Copy-Item -Force $nupkgFile $outDir
                        Write-Host "Chocolatey package copied to $outDir"
                    }
                }
            } finally {
                Set-Location $originalLocation
            }
        } else {
            Write-Warning "choco.exe not found in PATH. Skipping Chocolatey packaging."
        }
    } finally {
        Set-Content -Path $installScriptPath -Value $originalInstallScript -Encoding UTF8 -NoNewline
        if ($null -ne $originalVerification) {
            Set-Content -Path $verificationPath -Value $originalVerification -Encoding UTF8 -NoNewline
        } else {
            Remove-Item -Path $verificationPath -Force -ErrorAction SilentlyContinue
        }
    }
} else {
    Write-Warning "Skipping Chocolatey package because both x64 and arm64 MSIs are required. Missing: $(@($x64Msi, $arm64Msi) | Where-Object { -not (Test-Path $_) })"
}

Write-Host "Done. Artifacts available in: $outDir"
