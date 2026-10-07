using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using WindowsIsland.Services;

namespace WindowsIsland.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private const string Download = "https://github.com/PedroRuedas/Windows-Island/releases/download";
    private static readonly Version Current = new(0, 7, 2);
    private readonly string _folder = Directory.CreateTempSubdirectory("island-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static JsonElement Release(string tag, string? digest = "sha256:abc123", string? url = null, string asset = "WindowsIsland-Setup-0.8.0.exe")
    {
        var assetJson = new Dictionary<string, object?>
        {
            ["name"] = asset,
            ["browser_download_url"] = url ?? $"{Download}/{tag}/{asset}",
        };
        if (digest is not null)
            assetJson["digest"] = digest;
        object release = new
        {
            tag_name = tag,
            html_url = $"https://github.com/PedroRuedas/Windows-Island/releases/tag/{tag}",
            assets = new object[] { new { name = "notes.txt", browser_download_url = $"{Download}/{tag}/notes.txt" }, assetJson },
        };
        return JsonSerializer.SerializeToElement(release);
    }

    [Fact]
    public void NewerReleaseIsOffered()
    {
        var offer = UpdateService.ParseRelease(Release("v0.8.0"), Current, out string? problem);

        Assert.NotNull(offer);
        Assert.Null(problem);
        Assert.Equal(new Version(0, 8, 0), offer.Version);
        Assert.Equal($"{Download}/v0.8.0/WindowsIsland-Setup-0.8.0.exe", offer.InstallerUrl);
        Assert.Equal("abc123", offer.Sha256);
        Assert.EndsWith("/releases/tag/v0.8.0", offer.ReleaseUrl);
    }

    [Theory]
    [InlineData("v0.7.2")]
    [InlineData("v0.7.1")]
    [InlineData("not-a-version")]
    public void SameOrOlderReleaseIsSilentlyIgnored(string tag)
    {
        Assert.Null(UpdateService.ParseRelease(Release(tag), Current, out string? problem));
        Assert.Null(problem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("md5:abc123")]
    public void ReleaseWithoutSha256IsRejected(string? digest)
    {
        Assert.Null(UpdateService.ParseRelease(Release("v0.8.0", digest), Current, out string? problem));
        Assert.Contains("SHA-256", problem);
    }

    [Theory]
    [InlineData("https://github.com/someone-else/Windows-Island/releases/download/v0.8.0/WindowsIsland-Setup-0.8.0.exe")]
    [InlineData("https://evil.example/WindowsIsland-Setup-0.8.0.exe")]
    public void InstallerFromAnotherPlaceIsRejected(string url)
    {
        Assert.Null(UpdateService.ParseRelease(Release("v0.8.0", url: url), Current, out string? problem));
        Assert.Contains("endereço inesperado", problem);
    }

    [Fact]
    public void ReleaseWithoutInstallerIsRejected()
    {
        Assert.Null(UpdateService.ParseRelease(Release("v0.8.0", asset: "WindowsIsland-0.8.0.zip"), Current, out string? problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void FileWithDifferentHashIsRejected()
    {
        string file = WriteFile("installer.exe", [1, 2, 3]);

        Assert.False(UpdateService.Verify(file, new string('0', 64), PinnedCertificate(), out string problem));
        Assert.Contains("SHA-256", problem);
    }

    [Fact]
    public void UnsignedFileIsRejected()
    {
        string file = WriteFile("installer.exe", [1, 2, 3]);

        Assert.False(UpdateService.Verify(file, Sha256(file), PinnedCertificate(), out string problem));
        Assert.NotEmpty(problem);
    }

    [Fact]
    public void FileSignedByAnotherCertificateIsRejected()
    {
        // The runtime's own assemblies carry Microsoft's Authenticode signature: valid, but not ours.
        string file = File.Exists(typeof(object).Assembly.Location) ? typeof(object).Assembly.Location : throw new FileNotFoundException();
        string copy = Path.Combine(_folder, Path.GetFileName(file));
        File.Copy(file, copy);

        Assert.False(UpdateService.Verify(copy, Sha256(copy), PinnedCertificate(), out string problem));
        Assert.Contains("outro certificado", problem);
    }

    private string WriteFile(string name, byte[] content)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>A throwaway self-signed certificate standing in for the island's signing certificate.</summary>
    private string PinnedCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Windows Island Tests", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        string path = Path.Combine(_folder, "pinned.cer");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Cert));
        return path;
    }
}
