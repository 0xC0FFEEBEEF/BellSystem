using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SchoolBell.Contracts;

namespace SchoolBell.Server;

public static class DeploymentInstaller
{
    public const string Artifact = "deployment/SchoolBellDeploy.exe";

    // Open once, verify the publisher signature and template bytes, then stream this same file.
    public static (FileStream? Template, string? Version, string? Error) Open(ServerConfig cfg, ReleaseTrust trust)
    {
        var dir = string.IsNullOrEmpty(cfg.DeploymentBundlePath)
            ? Path.Combine(AppContext.BaseDirectory, "client-release") : cfg.DeploymentBundlePath;
        string manifest = Path.Combine(dir, "release-manifest.json"), signature = manifest + ".sig";
        if (!File.Exists(manifest) || !File.Exists(signature)) return (null, null, "Install a server release containing the Windows deployment installer.");
        var release = trust.Verify(File.ReadAllText(manifest), File.ReadAllText(signature));
        if (release is null || !release.Files.TryGetValue(Artifact, out var info))
            return (null, null, "The server release has no verified Windows deployment installer. Install a newer server release.");
        string path = Path.Combine(dir, "deployment", "SchoolBellDeploy.exe");
        if (!File.Exists(path)) return (null, null, "The Windows deployment installer is missing from the server release.");
        var stream = File.OpenRead(path);
        if (stream.Length != info.Bytes || !Convert.ToHexString(SHA256.HashData(stream)).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            stream.Dispose();
            return (null, null, "The Windows deployment installer failed its integrity check. Reinstall the server release.");
        }
        stream.Position = 0;
        return (stream, release.Version, null);
    }

    public static string? ReadCa(ServerConfig cfg)
    {
        if (string.IsNullOrEmpty(cfg.DeploymentCaPath) || !File.Exists(cfg.DeploymentCaPath)) return null;
        var bytes = File.ReadAllBytes(cfg.DeploymentCaPath);
        if (bytes.Length > 16 * 1024) throw new InvalidDataException("The school CA certificate is too large.");
        // Accept only a public CA certificate; never package a private key or leaf certificate.
        using var cert = bytes.AsSpan().StartsWith("-----BEGIN"u8)
            ? X509Certificate2.CreateFromPem(System.Text.Encoding.UTF8.GetString(bytes))
            : X509CertificateLoader.LoadCertificate(bytes);
        if (!cert.Extensions.OfType<X509BasicConstraintsExtension>().Any(x => x.CertificateAuthority))
            throw new InvalidDataException("The school certificate must be a CA certificate.");
        return Convert.ToBase64String(cert.Export(X509ContentType.Cert));
    }
}
