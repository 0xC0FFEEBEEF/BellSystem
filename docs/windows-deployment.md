# Windows deployment from Admin

Open **Admin → Enrollment**, enter a label, computer limit, and validity in hours,
then select **Download Windows Installer**. Deploy the downloaded `SchoolBellSetup.exe`
with the PDQ parameters shown on that page. Only the EXE is required on each PC.
Run the PDQ step as an administrator and accept exit code 0 only.

The EXE contains a fresh enrollment token and the server's school CA certificate,
when configured. The server address remains a `/SERVER=host:port` parameter.
For an interactive installation, the installer asks for the server address.
The token expires and can be revoked in Enrollment. Its expiration does not
interrupt computers that have already enrolled. Treat the download as private
because it permits new enrollments until it expires, is revoked, or runs out of uses.

Each full server release includes a publisher-signed reusable Windows deployment
launcher with the generic Windows installer embedded. Ubuntu appends the new
enrollment payload to that launcher at download time; it does not run a Windows
compiler or need the release signing private key. The original signed template
is checked against its manifest before every download. The personalized EXE is
not Authenticode signed.

`build/release.sh VERSION --iscc-vm schoolbell-lab` produces the deployment launcher
and includes it in the server bundle. A server built with `--skip-installer`, or
an older release without the launcher, shows an explanation and disables the
Windows installer download. The separate enrollment JSON remains available.

The default certificate path is `/etc/schoolbell/school-ca.cer`, populated by the
Ubuntu installer for Caddy's internal CA. For a different private CA, set
`SchoolBell__DeploymentCaPath` to a readable public CA certificate (PEM or DER).
A publicly trusted HTTPS server can omit this file. Never use a private key or
leaf/server certificate as the deployment CA. `SchoolBell__DeploymentBundlePath`
can override the location of the signed `client-release` directory for lab testing.

Deployment launcher errors return code 2 and write
`C:\ProgramData\SchoolBell\logs\deployment.log`. Setup retains its existing codes:
10 means installed but enrollment failed; 11 means service startup failed.
Use `/LOG="C:\Windows\Temp\SchoolBell-setup.log"` to capture setup diagnostics.
