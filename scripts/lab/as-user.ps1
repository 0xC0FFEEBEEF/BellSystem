# Lab helper: run $UserCode (PowerShell) inside the signed-in console user's session via a
# one-off interactive scheduled task, wait, and print its output. Caller sets $UserCode.
$user = (Get-CimInstance Win32_ComputerSystem).UserName
if (-not $user) { "no signed-in console user"; return }
$dir = "C:\Users\Public\sblab"; New-Item -ItemType Directory -Force $dir | Out-Null
$id = [guid]::NewGuid().ToString("N").Substring(0, 8)
$script = "$dir\u-$id.ps1"; $out = "$dir\u-$id.out"
Set-Content -Path $script -Value ("& {`n" + $UserCode + "`n} *> '$out'") -Encoding UTF8
$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName "sblab-$id" -Action $action -Principal $principal -Force | Out-Null
Start-ScheduledTask -TaskName "sblab-$id"
for ($i = 0; $i -lt 120; $i++) { Start-Sleep -Milliseconds 500; if ((Get-ScheduledTask -TaskName "sblab-$id").State -ne "Running" -and (Test-Path $out)) { break } }
Unregister-ScheduledTask -TaskName "sblab-$id" -Confirm:$false
if (Test-Path $out) { Get-Content $out } else { "no output" }
Remove-Item $script, $out -ErrorAction SilentlyContinue
