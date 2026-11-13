# Release 0.9.16 verification — 2026-11-13

Published with the owner's explicit instruction to build and publish when finished.

- Release: https://github.com/0xC0FFEEBEEF/BellSystem/releases/tag/v0.9.16
- Published: 2026-11-13 17:17:51 UTC.
- Source/tag/manifest commit: `64425e3461481971a7e8d59a441a5420ce117231`.
- Build: `nice -n 10 bash build/release.sh 0.9.16 --iscc-vm schoolbell-lab`.
- Server bundle SHA-256: `af3ba0e2f8efb7eead3b1e9fa51df3849d2171b60285c8689aa28526c4d12da1`.
- Windows installer SHA-256: `39a240217c0444d7fb695bc704970874ae5ba3e98c30a05e25857328b2e5678e`.

## Gates

170 server and 86 client tests passed in Release configuration. The resync regression tests
now await an actual resync response rather than assuming authentication means replay has finished.
Production web assets and self-contained Linux/Windows projects built successfully.

Both outer and embedded Windows release signatures verified with the committed public key.
All 325 entries in `SHA256SUMS` matched. The embedded installer matches the standalone installer;
both manifests identify the source commit above. Archive paths and required bundled executables,
model, updater helper and deployment launcher were checked. No enrollment or database files
were included. All eight GitHub asset sizes and SHA-256 digests matched local files, both before
and after publication. GitHub's latest release and remote tag identify v0.9.16 and the source above.

## Packaged runtime

Extracted the actual server release into a disposable Ubuntu 24.04 container, with an isolated
data directory, loopback ports and four CPUs. The server and bundled small.en model became ready.
The smoke check created a temporary admin and fake offline endpoint, then verified:

- Bell notification duration saves and appears in endpoint settings.
- Schedule publication creates revision 1 and a signed rule snapshot.
- A public speech recording reaches a completed transcript while the announcement offer remains
  active; the durable job is completed too. End-to-end elapsed time was 14.44 seconds in this run.
- The text reminder route accepts the completed transcript and counts the original offline audience.
- An HMAC-authenticated client log upload appears in endpoint-filtered admin logs.
- Admin's Whisper readiness probe sees the actual running model.

The private classroom recording was used only for the earlier local transcription check documented
in `transcription-2026-11-13.md`; neither that audio nor its speech text is in the release.

Windows service and companion metadata report 0.9.16 plus the source commit; installer metadata
reports 0.9.16. The first combined warmup exceeded the harness's 120-second deadline but subsequently
exited successfully. Repeated warmups using explicit process handles each completed within a
30-second deadline with exit code 0; the service printed `warmup ok 0.9.16`.

The container was removed and the Windows lab VM shut down. The school server and classroom
computers were not deployed by this release operation. The installer remains unsigned by
Authenticode; the SchoolBell manifests provide release integrity checks.
