# Lab helper: read/set the default render endpoint's master volume and mute (Core Audio COM).
param([string]$Action = "get", [double]$Volume = -1, [int]$Mute = -1)
if (-not ("LabAudio.Endpoint" -as [type])) {
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
namespace LabAudio {
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioEndpointVolume {
  int A(); int B(); int C(); int D();
  int SetMasterVolumeLevelScalar(float level, Guid ctx);
  int E();
  int GetMasterVolumeLevelScalar(out float level);
  int F(); int G(); int H(); int I();
  int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, Guid ctx);
  int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice { int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator { int NotImpl1(); int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev); }
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorComObject { }
public static class Endpoint {
  static IAudioEndpointVolume Vol() {
    var e = (IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject());
    IMMDevice d; Marshal.ThrowExceptionForHR(e.GetDefaultAudioEndpoint(0, 1, out d));
    var iid = typeof(IAudioEndpointVolume).GUID; object o;
    Marshal.ThrowExceptionForHR(d.Activate(ref iid, 23, IntPtr.Zero, out o));
    return (IAudioEndpointVolume)o;
  }
  public static float GetVolume() { float v; Marshal.ThrowExceptionForHR(Vol().GetMasterVolumeLevelScalar(out v)); return v; }
  public static bool GetMute() { bool m; Marshal.ThrowExceptionForHR(Vol().GetMute(out m)); return m; }
  public static void SetVolume(float v) { Marshal.ThrowExceptionForHR(Vol().SetMasterVolumeLevelScalar(v, Guid.Empty)); }
  public static void SetMute(bool m) { Marshal.ThrowExceptionForHR(Vol().SetMute(m, Guid.Empty)); }
}}
"@
}
if ($Volume -ge 0) { [LabAudio.Endpoint]::SetVolume([float]$Volume) }
if ($Mute -ge 0) { [LabAudio.Endpoint]::SetMute([bool]$Mute) }
"master_volume={0:N2} master_muted={1}" -f [LabAudio.Endpoint]::GetVolume(), [LabAudio.Endpoint]::GetMute()
