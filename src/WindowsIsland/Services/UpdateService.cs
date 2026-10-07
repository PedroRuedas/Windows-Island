using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace WindowsIsland.Services;

/// <summary>A newer release, downloaded and verified, waiting to be installed.</summary>
public sealed record PendingUpdate(Version Version, string InstallerPath, string Sha256, string ReleaseUrl);

/// <summary>
/// Keeps the island up to date from GitHub releases. Checks shortly after start and every few hours; when a newer
/// release exists it downloads the installer and only offers it if BOTH hold:
/// <list type="bullet">
/// <item>its SHA-256 matches the digest GitHub publishes for the asset;</item>
/// <item>it is Authenticode-signed by the same certificate as the installed package (package\WindowsIsland.cer).</item>
/// </list>
/// The signature pin means that even a compromised GitHub account can't push an installer the island would run.
/// Installing always goes through Windows' administrator prompt: the user approves every update.
/// </summary>
public sealed class UpdateService
{
    private const string Repo = "PedroRuedas/Windows-Island";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1), Interval = TimeSpan.FromHours(6);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly SemaphoreSlim _checking = new(1, 1);

    public UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WindowsIsland-Updater");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version CurrentVersion { get; } = typeof(UpdateService).Assembly.GetName().Version is { } v
        ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
        : new Version(0, 0, 0);

    /// <summary>The certificate the installer put next to the app. Dev builds have none, so they never self-update.</summary>
    private static string PinnedCertificate => Path.Combine(AppContext.BaseDirectory, "package", "WindowsIsland.cer");

    public static bool IsInstalledBuild => File.Exists(PinnedCertificate);

    private static string Folder => Path.Combine(App.DataDirectory, "updates");

    /// <summary>Raised (on a background thread) when a verified update is ready.</summary>
    public event Action<PendingUpdate>? UpdateReady;

    public void Start()
    {
        if (!IsInstalledBuild)
            return;
        // Installers from earlier updates (or abandoned offers) aren't needed anymore; a newer one is fetched again.
        if (Directory.Exists(Folder))
            foreach (var old in Directory.EnumerateFiles(Folder, "WindowsIsland-Setup-*.exe"))
                TryDelete(old);
        _ = LoopAsync();
    }

    private async Task LoopAsync()
    {
        await Task.Delay(FirstCheck);
        while (true)
        {
            await CheckAsync();
            await Task.Delay(Interval);
        }
    }

    /// <summary>Looks for a newer release. Returns the verified update, or null if up to date (or anything failed).</summary>
    public async Task<PendingUpdate?> CheckAsync()
    {
        if (!IsInstalledBuild || !await _checking.WaitAsync(0))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
            if (ParseRelease(doc.RootElement, CurrentVersion, out string? skipped) is not { } offer)
            {
                if (skipped is not null)
                    App.Log(skipped);
                return null;
            }
            var (latest, url, expectedHash, releaseUrl) = offer;

            Directory.CreateDirectory(Folder);
            foreach (var old in Directory.EnumerateFiles(Folder, "WindowsIsland-Setup-*.exe"))
                TryDelete(old);
            string path = Path.Combine(Folder, Path.GetFileName(new Uri(url).LocalPath));
            await using (var source = await _http.GetStreamAsync(url))
            await using (var target = File.Create(path))
                await source.CopyToAsync(target);

            if (!Verify(path, expectedHash, PinnedCertificate, out string problem))
            {
                App.Log($"Atualização {latest} descartada: {problem}");
                TryDelete(path);
                return null;
            }

            var update = new PendingUpdate(latest, path, expectedHash, releaseUrl);
            UpdateReady?.Invoke(update);
            return update;
        }
        catch (Exception ex)
        {
            // Offline, rate-limited, GitHub down…: try again next time.
            App.Log($"Verificação de atualização falhou: {ex.Message}");
            return null;
        }
        finally
        {
            _checking.Release();
        }
    }

    /// <summary>What a newer release offers: its version, the installer's address and published SHA-256, and its page.</summary>
    internal sealed record ReleaseOffer(Version Version, string InstallerUrl, string Sha256, string ReleaseUrl);

    /// <summary>
    /// Reads GitHub's "latest release" JSON. Returns null when it is not newer than <paramref name="current"/> (then
    /// <paramref name="problem"/> is null) or when it can't be trusted (then <paramref name="problem"/> says why).
    /// </summary>
    internal static ReleaseOffer? ParseRelease(JsonElement release, Version current, out string? problem)
    {
        problem = null;
        if (!Version.TryParse(release.GetProperty("tag_name").GetString()?.TrimStart('v'), out var latest) || latest <= current)
            return null;

        var asset = release.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => a.GetProperty("name").GetString() is { } name && name.StartsWith("WindowsIsland-Setup-") && name.EndsWith(".exe"));
        if (asset.ValueKind != JsonValueKind.Object
            || asset.TryGetProperty("digest", out var digest) is false
            || digest.GetString() is not { } digestText || !digestText.StartsWith("sha256:"))
        {
            problem = $"Atualização {latest}: instalador sem SHA-256 publicado, ignorada.";
            return null;
        }
        string url = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.Ordinal))
        {
            problem = $"Atualização {latest}: endereço inesperado ({url}), ignorada.";
            return null;
        }
        string releaseUrl = release.TryGetProperty("html_url", out var html) && html.GetString() is { } page ? page : $"https://github.com/{Repo}/releases";
        return new ReleaseOffer(latest, url, digestText["sha256:".Length..], releaseUrl);
    }

    /// <summary>Re-checks the file right before running it (it sits in a user-writable folder) and starts the installer.</summary>
    public static InstallResult Install(PendingUpdate update)
    {
        if (!File.Exists(update.InstallerPath))
            return InstallResult.Failed;
        if (!Verify(update.InstallerPath, update.Sha256, PinnedCertificate, out string problem))
        {
            App.Log($"Atualização {update.Version} não instalada: {problem}");
            TryDelete(update.InstallerPath);
            return InstallResult.Failed;
        }
        try
        {
            // /RELAUNCH=1: the installer reopens the island when it's done (see installer\WindowsIsland.iss).
            Process.Start(new ProcessStartInfo(update.InstallerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1")
            {
                UseShellExecute = true, // shows Windows' administrator prompt (the installer requires admin)
            });
            return InstallResult.Started;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return InstallResult.Declined; // The user said no to the administrator prompt.
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return InstallResult.Failed;
        }
    }

    public enum InstallResult { Started, Declined, Failed }

    internal static bool Verify(string path, string expectedHash, string pinnedCertificate, out string problem)
    {
        using (var stream = File.OpenRead(path))
        {
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                problem = "SHA-256 diferente do publicado no GitHub";
                return false;
            }
        }

        // The signature must be intact (WinVerifyTrust checks the file against it)…
        int trust = WinVerifyTrust(path);
        // …and our certificate is self-signed, so "untrusted root" is the expected answer; anything else is not.
        if (trust != 0 && trust != CERT_E_UNTRUSTEDROOT)
        {
            problem = $"assinatura inválida (0x{trust:X8})";
            return false;
        }

        // …and made by the same certificate as the installed package.
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the documented way to read an Authenticode signer.
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            using var pinned = new X509Certificate2(pinnedCertificate);
#pragma warning restore SYSLIB0057
            if (!signer.Thumbprint.Equals(pinned.Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                problem = $"assinado por outro certificado ({signer.Subject})";
                return false;
            }
        }
        catch (CryptographicException)
        {
            problem = "instalador sem assinatura";
            return false;
        }

        problem = "";
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ── WinVerifyTrust ──

    private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

    private static int WinVerifyTrust(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, filePtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (self-signed: nothing to check against)
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 0,       // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x10,      // WTD_CACHE_ONLY_URL_RETRIEVAL: never go online
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(filePtr);
            Marshal.FreeHGlobal(filePtr);
        }
    }
}
