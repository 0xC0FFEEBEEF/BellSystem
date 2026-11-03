# Lab helper (run in the user's session): list/mute audio sessions via the installed NAudio.
# Caller sets $ProcName and $SetMute (-1 leave, 0/1).
Add-Type -Path "C:\Program Files\SchoolBell\NAudio.Core.dll"
Add-Type -Path "C:\Program Files\SchoolBell\NAudio.Wasapi.dll"
$e = New-Object NAudio.CoreAudioApi.MMDeviceEnumerator
$d = $e.GetDefaultAudioEndpoint([NAudio.CoreAudioApi.DataFlow]::Render, [NAudio.CoreAudioApi.Role]::Multimedia)
$want = 0; if ($ProcName) { $p = Get-Process $ProcName -ErrorAction SilentlyContinue | Select -First 1; if ($p) { $want = $p.Id } }
$ss = $d.AudioSessionManager.Sessions
for ($i = 0; $i -lt $ss.Count; $i++) {
  $s = $ss[$i]; $sp = $s.GetProcessID
  if ($want -ne 0 -and $sp -ne $want) { continue }
  if ($SetMute -ge 0) { $s.SimpleAudioVolume.Mute = [bool]$SetMute }
  "pid={0} session_muted={1} session_volume={2:N2} state={3}" -f $sp, $s.SimpleAudioVolume.Mute, $s.SimpleAudioVolume.Volume, $s.State
}
