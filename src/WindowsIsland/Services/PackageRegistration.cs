using System.Diagnostics;
using System.IO;
using System.Windows;
using global::Windows.Management.Deployment;

namespace WindowsIsland.Services;

/// <summary>
/// Registers the sparse package (Package/AppxManifest.xml, copied next to the exe) so that this exe gets a
/// package identity, which Windows requires for reading notifications. Unsigned registration only works with
/// Windows Developer Mode on; a signed .msix would be needed to ship it to other people.
/// </summary>
public static class PackageRegistration
{
    private const string PackageName = "WindowsIsland";
    private const string Publisher = "CN=WindowsIsland";
    public const string RestartArgument = "--restart";

    public static bool HasIdentity
    {
        get
        {
            try
            {
                return global::Windows.ApplicationModel.Package.Current is not null;
            }
            catch
            {
                // InvalidOperationException: "The process has no package identity."
                return false;
            }
        }
    }

    public static async Task RegisterAsync()
    {
        string directory = AppContext.BaseDirectory;
        string manifest = Path.Combine(directory, "AppxManifest.xml");
        if (!File.Exists(manifest))
            throw new FileNotFoundException("AppxManifest.xml não encontrado ao lado do executável.", manifest);

        var manager = new PackageManager();

        // A previous registration may point at another folder (e.g. Debug vs. publish); replace it.
        foreach (var existing in manager.FindPackagesForUser("", PackageName, Publisher))
            await manager.RemovePackageAsync(existing.Id.FullName);

        var result = await manager.RegisterPackageByUriAsync(new Uri(manifest), new RegisterPackageOptions
        {
            ExternalLocationUri = new Uri(directory),
            DeveloperMode = true,
        });
        if (result.ExtendedErrorCode is { } error)
            throw new InvalidOperationException(result.ErrorText, error);
    }

    /// <summary>Identity is attached at process creation, so the island must relaunch itself.</summary>
    public static void Restart()
    {
        if (Environment.ProcessPath is not { } exe)
            return;
        Process.Start(new ProcessStartInfo(exe, RestartArgument) { UseShellExecute = false });
        Application.Current.Shutdown();
    }
}
