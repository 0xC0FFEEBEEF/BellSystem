using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SchoolBell.Contracts;
using SchoolBell.Server;

namespace Server.Tests;

public class DeploymentInstallerTests
{
    private static void Template(TestApp app, byte[] bytes)
    {
        var cfg = app.Factory.Services.GetRequiredService<ServerConfig>();
        cfg.DeploymentBundlePath = Path.Combine(app.Dir, "deployment-bundle");
        cfg.ReleaseKeyPath = Path.Combine(app.Dir, "publisher.pem");
        cfg.DeploymentCaPath = "";
        Directory.CreateDirectory(Path.Combine(cfg.DeploymentBundlePath, "deployment"));
        File.WriteAllBytes(Path.Combine(cfg.DeploymentBundlePath, DeploymentInstaller.Artifact), bytes);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(cfg.ReleaseKeyPath, key.ExportSubjectPublicKeyInfoPem());
        var manifest = JsonSerializer.Serialize(new ReleaseManifest("SchoolBell", "0.9.1", "test", 1,
            new() { [DeploymentInstaller.Artifact] = new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length) }), ControlEnvelope.Json);
        File.WriteAllText(Path.Combine(cfg.DeploymentBundlePath, "release-manifest.json"), manifest);
        File.WriteAllText(Path.Combine(cfg.DeploymentBundlePath, "release-manifest.json.sig"),
            Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(manifest), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)));
    }

    [Fact]
    public async Task Download_contains_usable_limited_enrollment_and_CA_but_no_server_address()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        byte[] template = "MZ-test-launcher"u8.ToArray();
        Template(app, template);
        var cfg = app.Factory.Services.GetRequiredService<ServerConfig>();
        using var key = ECDsa.Create();
        var req = new CertificateRequest("CN=School test CA", key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        cfg.DeploymentCaPath = Path.Combine(app.Dir, "school-ca.cer");
        File.WriteAllText(cfg.DeploymentCaPath, cert.ExportCertificatePem());
        var response = await app.Post("/api/admin/enrollment-installer", new { label = "PDQ", max_uses = 1, expires_hours = 2 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("SchoolBellSetup.exe", response.Content.Headers.ContentDisposition!.ToString());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(template, bytes[..template.Length]);
        var payload = DeploymentPackage.Read(new MemoryStream(bytes));
        Assert.Equal(cert.Export(X509ContentType.Cert), Convert.FromBase64String(payload.CaCertificateBase64!));
        using var json = JsonDocument.Parse(payload.EnrollmentJson);
        Assert.False(json.RootElement.TryGetProperty("server", out _));
        string token = json.RootElement.GetProperty("token").GetString()!;
        var body = new { token, device_name = "one", client_version = "0.9.1" };
        Assert.Equal(HttpStatusCode.Created, (await app.Post("/api/enroll", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Post("/api/enroll", new { token, device_name = "two" })).StatusCode);
        Assert.DoesNotContain(token, app.Logs.AllText);
    }

    [Fact]
    public async Task Missing_or_tampered_template_does_not_create_a_token()
    {
        using var app = new TestApp();
        await app.LoginAsAdmin();
        var cfg = app.Factory.Services.GetRequiredService<ServerConfig>();
        cfg.DeploymentBundlePath = app.Dir;
        Assert.Equal(HttpStatusCode.Conflict, (await app.Post("/api/admin/enrollment-installer", new { })).StatusCode);
        Template(app, "MZ-test-launcher"u8.ToArray());
        File.AppendAllText(Path.Combine(cfg.DeploymentBundlePath, DeploymentInstaller.Artifact), "tampered");
        Assert.Equal(HttpStatusCode.Conflict, (await app.Post("/api/admin/enrollment-installer", new { })).StatusCode);
        var tokens = await app.Json(await app.Get("/api/admin/enrollment-tokens"));
        Assert.Equal(0, tokens.GetArrayLength());
    }

    [Fact]
    public async Task Installer_download_requires_admin_and_csrf()
    {
        using var app = new TestApp();
        Assert.NotEqual(HttpStatusCode.OK, (await app.SendBare("POST", "/api/admin/enrollment-installer", new { })).StatusCode);
        await app.LoginAsAdmin();
        await app.CreateUser("viewer", "viewer-password-12", "viewer");
        using var viewer = new TestApp.Session(app);
        await viewer.LoginAs("viewer", "viewer-password-12");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.Post("/api/admin/enrollment-installer", new { })).StatusCode);
    }

    [Fact]
    public void Malformed_and_oversized_payloads_are_rejected()
    {
        Assert.Throws<InvalidDataException>(() => DeploymentPackage.Read(new MemoryStream("MZ"u8.ToArray())));
        using var output = new MemoryStream();
        output.Write("MZ"u8);
        DeploymentPackage.Append(output, new("{\"token\":\"test\",\"server\":\"https://wrong\"}", null));
        Assert.Throws<InvalidDataException>(() => DeploymentPackage.Read(output));
        Assert.Throws<InvalidDataException>(() => DeploymentPackage.Append(new MemoryStream(), new(new string('a', DeploymentPackage.MaxPayloadBytes), null)));
    }
}
