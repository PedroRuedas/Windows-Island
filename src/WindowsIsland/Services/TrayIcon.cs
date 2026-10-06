using Forms = System.Windows.Forms;
using WindowsIsland.Core;

namespace WindowsIsland.Services;

/// <summary>
/// The black-hole icon in the notification area (the "hidden icons" chevron): click it to swallow the island
/// completely, click again to bring it back. Right-click for the menu.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _toggle;
    private readonly Forms.ToolStripMenuItem _startup;

    public TrayIcon()
    {
        _toggle = new Forms.ToolStripMenuItem("Ocultar ilha", null, (_, _) => ToggleRequested?.Invoke()) { Font = BoldMenuFont() };
        _startup = new Forms.ToolStripMenuItem("Iniciar com o Windows", null, (_, _) => ToggleStartup());

        var menu = new Forms.ContextMenuStrip { RenderMode = Forms.ToolStripRenderMode.System };
        menu.Items.Add(_toggle);
        menu.Items.Add(_startup);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Sair", null, (_, _) => ExitRequested?.Invoke()));
        menu.Opening += (_, _) => _startup.Checked = StartupRegistration.IsEnabled;

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Windows Island",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ToggleRequested?.Invoke();
        };
    }

    public event Action? ToggleRequested;
    public event Action? ExitRequested;

    public void SetIslandVisible(bool visible)
    {
        _toggle.Text = visible ? "Ocultar ilha" : "Mostrar ilha";
        _icon.Text = visible ? "Windows Island" : "Windows Island (oculta): clique para mostrar";
    }

    private static void ToggleStartup()
    {
        try
        {
            StartupRegistration.Set(!StartupRegistration.IsEnabled);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Resources/blackhole.ico"));
        using var stream = resource!.Stream;
        // Ask for the tray size of the current DPI so Windows doesn't rescale a 32px frame down to 16.
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    private static System.Drawing.Font BoldMenuFont() =>
        new(Forms.SystemInformation.MenuFont, System.Drawing.FontStyle.Bold);

    public void Dispose()
    {
        // Without this the icon lingers in the tray until the mouse passes over it.
        _icon.Visible = false;
        _icon.Dispose();
    }
}
