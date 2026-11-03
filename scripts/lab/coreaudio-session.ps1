# Lab helper: list/mute per-process audio sessions on the default render endpoint.
# Vars set by caller: $ProcName (e.g. SchoolBellCompanion), $SetMute (-1 = leave, 0/1)
if (-not ("LabAudio.Sessions3" -as [type])) {
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
namespace LabAudio {
[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice { int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator { int NotImpl1(); int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev); }
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorComObject { }
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2 { int A(); int B(); int GetSessionEnumerator(out IAudioSessionEnumerator e); }
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator { int GetCount(out int n); int GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object s); }
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2 { int A(); int B(); int C(); int D(); int E(); int F(); int G(); int H(); int I();
  int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string s);
  int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string s);
  int GetProcessId(out uint pid); }
[Guid("87CE5498-68D6-44E5-9215-6F9A6D7E5AF0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume { int SetMasterVolume(float v, ref Guid g); int GetMasterVolume(out float v);
  int SetMute([MarshalAs(UnmanagedType.Bool)] bool m, ref Guid g); int GetMute([MarshalAs(UnmanagedType.Bool)] out bool m); }
public static class Sessions3 {
  public static List<string> List(uint onlyPid, int setMute) {
    List<string> r = null; Exception err = null;
    var t = new System.Threading.Thread(() => { try { r = ListMta(onlyPid, setMute); } catch (Exception ex) { err = ex; } });
    t.SetApartmentState(System.Threading.ApartmentState.MTA); t.Start(); t.Join();
    if (err != null) throw err; return r;
  }
  static List<string> ListMta(uint onlyPid, int setMute) {
    var res = new List<string>();
    var e = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
    IMMDevice d; Marshal.ThrowExceptionForHR(e.GetDefaultAudioEndpoint(0, 1, out d));
    var iid = typeof(IAudioSessionManager2).GUID; object o;
    Marshal.ThrowExceptionForHR(d.Activate(ref iid, 23, IntPtr.Zero, out o));
    IAudioSessionEnumerator en; ((IAudioSessionManager2)o).GetSessionEnumerator(out en);
    int n; en.GetCount(out n);
    for (int i = 0; i < n; i++) {
      object so; en.GetSession(i, out so); var s = (IAudioSessionControl2)so; uint pid; s.GetProcessId(out pid);
      if (onlyPid != 0 && pid != onlyPid) continue;
      var v = (ISimpleAudioVolume)so; var g = Guid.Empty;
      if (setMute >= 0) v.SetMute(setMute == 1, ref g);
      bool m; float vol; v.GetMute(out m); v.GetMasterVolume(out vol);
      res.Add(String.Format("pid={0} session_muted={1} session_volume={2:N2}", pid, m, vol));
    }
    return res;
  }
}}
"@
}
$procId = 0
if ($ProcName) { $p = Get-Process $ProcName -ErrorAction SilentlyContinue | Select -First 1; if ($p) { $procId = [uint32]$p.Id } else { "no process $ProcName"; return } }
[LabAudio.Sessions3]::List([uint32]$procId, [int]$SetMute)
