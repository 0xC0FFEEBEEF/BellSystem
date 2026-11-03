# Lab-only manual deployment (mirrors what SchoolBellSetup.exe does) for the Gate A audio proof.
param([string]$Share = "Z:\", [string]$App = "$env:ProgramFiles\SchoolBell")
$ErrorActionPreference = "Stop"
$data = "$env:ProgramData\SchoolBell"
Stop-Service SchoolBell -ErrorAction SilentlyContinue
Get-Process SchoolBellCompanion -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force $App | Out-Null
robocopy (Join-Path $Share "app") $App /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if (Test-Path (Join-Path $Share "release-signing.pub.pem")) { Copy-Item (Join-Path $Share "release-signing.pub.pem") $App -Force }
Import-Certificate -FilePath (Join-Path $Share "school-ca.cer") -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
# ProgramData\SchoolBell: SYSTEM + Administrators only (device credential lives here)
New-Item -ItemType Directory -Force "$data\data\cache" | Out-Null
icacls $data /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null
# the signed-in user's companion reads cached bell sounds only
icacls "$data\data" /grant "*S-1-5-32-545:(X)" | Out-Null
icacls "$data\data\cache" /grant "*S-1-5-32-545:(OI)(CI)RX" | Out-Null
if (-not (Test-Path "$data\config.json")) {
  & "$App\EnrollTool.exe" --token-file (Join-Path $Share "schoolbell-enroll.json") --out "$data\config.json"
  if ($LASTEXITCODE -ne 0) { throw "enrollment failed ($LASTEXITCODE)" }
}
if (-not (Get-Service SchoolBell -ErrorAction SilentlyContinue)) {
  sc.exe create SchoolBell binPath= "`"$App\SchoolBellService.exe`"" start= auto DisplayName= "SchoolBell" | Out-Null
  sc.exe description SchoolBell "SchoolBell: school bells and announcements (connection, bell cache, keep-awake)." | Out-Null
  sc.exe failure SchoolBell reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
}
Copy-Item "$Share\SchoolBellCompanion.task.xml" "$env:TEMP\sbtask.xml" -Force; Register-ScheduledTask -TaskName "SchoolBellCompanion" -Xml (Get-Content "$env:TEMP\sbtask.xml" -Raw) -Force | Out-Null
Start-Service SchoolBell
Start-ScheduledTask -TaskName "SchoolBellCompanion"
Start-Sleep 5
Get-Service SchoolBell | Format-List Name,Status,StartType
Get-Process SchoolBellCompanion -ErrorAction SilentlyContinue | Select-Object Id,SessionId,ProcessName | Format-Table
