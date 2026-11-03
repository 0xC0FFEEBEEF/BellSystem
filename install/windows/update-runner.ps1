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
function RunSetup([string]$exe, [string]$tag) {
  Log "running $tag installer $exe"
  $p = Start-Process -FilePath $exe -ArgumentList "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/LOG=`"$dir\setup-$tag.log`"" -Wait -PassThru
  Log "$tag installer exit code $($p.ExitCode)"
  return $p.ExitCode
}

Log "update $FromVersion -> $ToVersion starting"
Start-Sleep -Seconds 2
$t0 = Now
$rc = RunSetup $Setup "new-$ToVersion"
if ($rc -eq 0 -and (Healthy $ToVersion $t0)) { Result "installed" $null; exit 0 }
Log "new version not healthy (installer exit $rc); rolling back"
if ($Previous -and (Test-Path $Previous)) {
  $t1 = Now
  $rc2 = RunSetup $Previous "rollback-$FromVersion"
  if (Healthy $FromVersion $t1) { Result "rolled_back" "version $ToVersion did not reconnect (installer exit $rc); $FromVersion restored"; exit 1 }
  Result "failed" "update failed and the rollback installer exit code was $rc2"
  exit 2
}
Result "failed" "update failed (installer exit $rc) and no previous installer was available"
exit 2
