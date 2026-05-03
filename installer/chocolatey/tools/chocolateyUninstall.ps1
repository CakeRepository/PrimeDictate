$ErrorActionPreference = 'Stop'
$silentArgs = '/qn /norestart'
$validExitCodes = @(0, 3010, 1605, 1614, 1641)
$msiExec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
$productCodePattern = '^\{[0-9A-Fa-f\-]{36}\}$'

$installedProduct = Get-UninstallRegistryKey -SoftwareName 'PrimeDictate*' |
  Where-Object { $_.PSChildName -match $productCodePattern } |
  Sort-Object DisplayVersion -Descending |
  Select-Object -First 1

if ($installedProduct) {
  $productCode = $installedProduct.PSChildName
  Write-Host "Uninstalling PrimeDictate via product code $productCode"
  Start-ChocolateyProcessAsAdmin `
    -ExeToRun $msiExec `
    -Statements "/x $productCode $silentArgs" `
    -ValidExitCodes $validExitCodes
  return
}

Write-Warning "No installed PrimeDictate product code found. Skipping MSI uninstall step."
