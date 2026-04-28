$ErrorActionPreference = 'Stop'
$toolsDir   = $(Split-Path -parent $MyInvocation.MyCommand.Definition)
$fileLocation = Join-Path $toolsDir 'PrimeDictate-Online.msi'
$packageParameters = Get-PackageParameters

$launchAtLogin = $true
if ($packageParameters.ContainsKey('NoLaunchAtLogin')) {
  $launchAtLogin = $false
} elseif ($packageParameters.ContainsKey('LaunchAtLogin')) {
  $launchAtLoginValue = [string]$packageParameters['LaunchAtLogin']
  $launchAtLogin = $launchAtLoginValue -notmatch '^(0|false|no|off)$'
}

$launchAtLoginProperty = if ($launchAtLogin) { '1' } else { '0' }

$packageArgs = @{
  packageName    = 'primedictate'
  fileType       = 'msi'
  file           = $fileLocation
  silentArgs     = "/qn /norestart LAUNCHATLOGIN=$launchAtLoginProperty"
  validExitCodes = @(0, 3010, 1641)
}

Install-ChocolateyInstallPackage @packageArgs
