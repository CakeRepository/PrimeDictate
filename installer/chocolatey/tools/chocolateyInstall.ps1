$ErrorActionPreference = 'Stop'
$packageParameters = Get-PackageParameters
$packageVersion = if ([string]::IsNullOrWhiteSpace($env:ChocolateyPackageVersion)) { '4.4.0' } else { $env:ChocolateyPackageVersion }
$releaseTag = "v$packageVersion"
$x64Url = "https://github.com/CakeRepository/PrimeDictate/releases/download/$releaseTag/PrimeDictate-Setup-$releaseTag-x64.msi"
$arm64Url = "https://github.com/CakeRepository/PrimeDictate/releases/download/$releaseTag/PrimeDictate-Setup-$releaseTag-arm64.msi"
$x64Checksum = '__PRIMEDICTATE_X64_SHA256__'
$arm64Checksum = '__PRIMEDICTATE_ARM64_SHA256__'

function Get-PrimeDictateNativeArchitecture {
  try {
    $processor = Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop | Select-Object -First 1
    if ($processor -and $null -ne $processor.Architecture) {
      switch ([int]$processor.Architecture) {
        9 { return 'x64' }
        12 { return 'arm64' }
      }
    }
  } catch {
    try {
      $processor = Get-WmiObject -Class Win32_Processor -ErrorAction Stop | Select-Object -First 1
      if ($processor -and $null -ne $processor.Architecture) {
        switch ([int]$processor.Architecture) {
          9 { return 'x64' }
          12 { return 'arm64' }
        }
      }
    } catch {
      # Fall through to environment variables below.
    }
  }

  $architectureValues = @($env:PROCESSOR_ARCHITEW6432, $env:PROCESSOR_ARCHITECTURE)
  foreach ($architecture in $architectureValues) {
    if ([string]::IsNullOrWhiteSpace($architecture)) {
      continue
    }

    if ($architecture -match '^(ARM64|AARCH64)$') {
      return 'arm64'
    }

    if ($architecture -match '^(AMD64|IA64|X64)$') {
      return 'x64'
    }
  }

  throw "Unsupported Windows processor architecture. PROCESSOR_ARCHITEW6432='$env:PROCESSOR_ARCHITEW6432', PROCESSOR_ARCHITECTURE='$env:PROCESSOR_ARCHITECTURE'."
}

$launchAtLogin = $true
if ($packageParameters.ContainsKey('NoLaunchAtLogin')) {
  $launchAtLogin = $false
} elseif ($packageParameters.ContainsKey('LaunchAtLogin')) {
  $launchAtLoginValue = [string]$packageParameters['LaunchAtLogin']
  $launchAtLogin = $launchAtLoginValue -notmatch '^(0|false|no|off)$'
}

$launchAtLoginProperty = if ($launchAtLogin) { '1' } else { '0' }
$nativeArchitecture = Get-PrimeDictateNativeArchitecture
$installerUrl = if ($nativeArchitecture -eq 'arm64') { $arm64Url } else { $x64Url }
$installerChecksum = if ($nativeArchitecture -eq 'arm64') { $arm64Checksum } else { $x64Checksum }

if ($installerChecksum -match '^__PRIMEDICTATE_.*_SHA256__$') {
  throw "PrimeDictate package checksums were not stamped during packaging."
}

Write-Host "Installing PrimeDictate using the $nativeArchitecture MSI from $installerUrl."

$packageArgs = @{
  packageName    = 'primedictate'
  fileType       = 'msi'
  url            = $installerUrl
  silentArgs     = "/qn /norestart LAUNCHATLOGIN=$launchAtLoginProperty"
  validExitCodes = @(0, 3010, 1641)
  checksum       = $installerChecksum
  checksumType   = 'sha256'
}
Install-ChocolateyPackage @packageArgs
