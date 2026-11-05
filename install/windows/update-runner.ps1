# SchoolBell client update runner (started by the SchoolBell service, runs as SYSTEM).
# Installs a publisher-signed SchoolBellSetup.exe that the service already verified, waits for
# the NEW service to authenticate to the school server (health.json), and reinstalls the
# previous version if it does not (automatic rollback). Result goes to -ResultFile for the next
# service start to report as update.status.
param(
  [Parameter(Mandatory)] [string]$Setup,
  [string]$Previous = "",
  [Parameter(Mandatory)] [string]$FromVersion,
  [Parameter(Mandatory)] [string]$ToVersion,
  [Parameter(Mandatory)] [string]$DataDir,
  [Parameter(Mandatory)] [string]$ResultFile,
  [int]$HealthTimeoutS = 300
)
$ErrorActionPreference = "Continue"
$dir = Split-Path $ResultFile
$log = Join-Path $dir "update-runner.log"
function Log([string]$m) { Add-Content -Path $log -Value ("{0:o} {1}" -f [DateTime]::UtcNow, $m) }
function Now() { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
function Result([string]$state, [string]$err) {
  $o = [ordered]@{ version = $ToVersion; state = $state; error = $err; at = (Now) }
  ($o | ConvertTo-Json -Compress) | Set-Content -Path $ResultFile -Encoding UTF8
  Log "result: $state $err"
}
function Healthy([string]$ver, [long]$sinceMs) {
  $health = Join-Path $DataDir "health.json"
  $deadline = (Get-Date).AddSeconds($HealthTimeoutS)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    try {
      $h = Get-Content $health -Raw -ErrorAction Stop | ConvertFrom-Json
      if ($h.version -eq $ver -and [long]$h.connected_at -ge $sinceMs) { return $true }
    } catch { }
  }
  return $false
}
# Setup replaces files the service and the tray app have open. Stop both and wait until the
# processes are really gone: a file still held makes silent setup abort (exit code 5).
function StopOurProcesses() {
  & sc.exe stop SchoolBell 2>&1 | Out-Null
  $deadline = (Get-Date).AddSeconds(60)
  while ((Get-Date) -lt $deadline) {
    Get-Process -Name SchoolBellCompanion -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $svc = Get-Service SchoolBell -ErrorAction SilentlyContinue
    $left = @(Get-Process -Name SchoolBellCompanion, SchoolBellService -ErrorAction SilentlyContinue)
    if ((-not $svc -or $svc.Status -eq 'Stopped') -and $left.Count -eq 0) { return }
    if ($svc -and $svc.Status -eq 'Stopped') { $left | Stop-Process -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
  }
  Log "warning: SchoolBell processes still running after 60 s"
}
function RunSetupOnce([string]$exe, [string]$tag) {
  StopOurProcesses
  Start-Sleep -Seconds 2
  Log "running $tag installer $exe"
  $p = Start-Process -FilePath $exe -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/LOG=`"$dir\setup-$tag.log`"" -PassThru
  $null = $p.Handle     # without this, ExitCode can come back empty (seen in the field)
  $p.WaitForExit()
  $code = $p.ExitCode
  Log "$tag installer exit code $code"
  return $code
}
# Exit code 5 = setup aborted, nearly always a file still in use: wait and try once more.
function RunSetup([string]$exe, [string]$tag) {
  $code = RunSetupOnce $exe $tag
  if ($code -eq 5) {
    Log "$tag installer aborted (a file was in use); retrying in 20 s"
    Start-Sleep -Seconds 20
    $code = RunSetupOnce $exe "$tag-retry"
  }
  return $code
}
# Whatever happened, never leave the computer without its SchoolBell service running.
function EnsureServiceRunning() {
  $svc = Get-Service SchoolBell -ErrorAction SilentlyContinue
  if (-not $svc) { Log "SchoolBell service is not installed"; return }
  if ($svc.Status -ne 'Running') {
    Log "starting the SchoolBell service (it was $($svc.Status))"
    try { Start-Service SchoolBell -ErrorAction Stop; $svc.WaitForStatus('Running', '00:02:00') } catch { Log "could not start the SchoolBell service: $_" }
  }
  & schtasks.exe /run /tn SchoolBellCompanion 2>&1 | Out-Null
}

Log "update $FromVersion -> $ToVersion starting"
Start-Sleep -Seconds 2
$t0 = Now
$rc = RunSetup $Setup "new-$ToVersion"
if ($rc -eq 0 -and (Healthy $ToVersion $t0)) { Result "installed" $null; EnsureServiceRunning; exit 0 }
Log "new version not healthy (installer exit $rc); rolling back"
if ($Previous -and (Test-Path $Previous)) {
  $t1 = Now
  $rc2 = RunSetup $Previous "rollback-$FromVersion"
  if (Healthy $FromVersion $t1) { Result "rolled_back" "version $ToVersion did not reconnect (installer exit $rc); $FromVersion restored"; EnsureServiceRunning; exit 1 }
  Result "failed" "update failed and the rollback installer exit code was $rc2"
  EnsureServiceRunning
  exit 2
}
Result "failed" "update failed (installer exit $rc) and no previous installer was available"
EnsureServiceRunning
exit 2
