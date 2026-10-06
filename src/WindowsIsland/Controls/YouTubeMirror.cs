using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WindowsIsland.Services;

namespace WindowsIsland.Controls;

/// <summary>
/// A muted YouTube player that mirrors the video playing in the browser: same video, kept in sync with the
/// browser's position and play/pause (the sound keeps coming from the browser).
/// Uses WebView2CompositionControl because a regular WebView2 is a child HWND, which cannot render inside the
/// island's transparent (layered) window.
/// </summary>
public sealed class YouTubeMirror
{
    private const string Host = "island.player";
    private const double MaxDriftSeconds = 1.5;

    private Task? _initialization;
    private string? _videoId;
    private bool _busy;

    public YouTubeMirror()
    {
        View = new WebView2CompositionControl
        {
            DefaultBackgroundColor = System.Drawing.Color.Black,
            // The island handles hover/clicks; the mirror is display-only.
            IsHitTestVisible = false,
            Focusable = false,
        };
    }

    public WebView2CompositionControl View { get; }

    /// <summary>The embed refused to play (owner disabled embedding, region lock...).</summary>
    public event Action<string>? Failed;

    /// <summary>Shows <paramref name="videoId"/> at the browser's position and follows it. UI thread.</summary>
    public async Task ShowAsync(string videoId, TimeSpan position, bool playing)
    {
        if (!YouTubeResolver.VideoIdRegex().IsMatch(videoId) || _busy)
            return;
        _busy = true;
        try
        {
            await (_initialization ??= InitializeAsync());
            if (videoId != _videoId)
            {
                _videoId = videoId;
                await Run($"island.load('{videoId}', {Seconds(position)}, {(playing ? "true" : "false")})");
                return;
            }
            await SyncAsync(position, playing);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Stops decoding while the island is collapsed. UI thread.</summary>
    public async Task PauseAsync()
    {
        if (_initialization is not { IsCompletedSuccessfully: true } || _videoId is null || _busy)
            return;
        try
        {
            await Run("island.pause()");
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private async Task SyncAsync(TimeSpan target, bool playing)
    {
        string result = await Run("island.time()");
        if (!double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out double current) || current < 0)
            return;
        if (Math.Abs(current - target.TotalSeconds) > MaxDriftSeconds)
            await Run($"island.seek({Seconds(target)})");
        await Run(playing ? "island.play()" : "island.pause()");
    }

    private async Task InitializeAsync()
    {
        string folder = Path.Combine(App.DataDirectory, "player");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "player.html"), PlayerHtml);

        var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(App.DataDirectory, "WebView2"));
        await View.EnsureCoreWebView2Async(environment);

        var core = View.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.IsMuted = true;
        // Served from a real https origin: YouTube embeds refuse to play without a referrer.
        core.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnMessage;

        var loaded = new TaskCompletionSource();
        core.NavigationCompleted += (_, _) => loaded.TrySetResult();
        core.Navigate($"https://{Host}/player.html");
        await loaded.Task;
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            if (doc.RootElement.GetProperty("type").GetString() == "error" && _videoId is { } id)
            {
                App.Log($"YouTube: o vídeo {id} não pode ser incorporado (erro {doc.RootElement.GetProperty("code")})");
                _videoId = null;
                Failed?.Invoke(id);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private Task<string> Run(string script) => View.CoreWebView2.ExecuteScriptAsync(script);

    private static string Seconds(TimeSpan t) => Math.Max(0, t.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    private const string PlayerHtml = """
        <!doctype html>
        <html>
        <head>
          <meta charset="utf-8">
          <style>
            html, body { margin: 0; height: 100%; background: #000; overflow: hidden; }
            #p, iframe { position: absolute; inset: 0; width: 100%; height: 100%; border: 0; }
          </style>
        </head>
        <body>
          <div id="p"></div>
          <script>
            var player = null, ready = false, pending = null;
            var api = document.createElement('script');
            api.src = 'https://www.youtube.com/iframe_api';
            document.head.appendChild(api);

            function post(message) { if (window.chrome && chrome.webview) chrome.webview.postMessage(message); }

            function onYouTubeIframeAPIReady() {
              player = new YT.Player('p', {
                width: '100%', height: '100%',
                playerVars: { controls: 0, disablekb: 1, fs: 0, iv_load_policy: 3, playsinline: 1, rel: 0, mute: 1 },
                events: {
                  onReady: function () {
                    ready = true;
                    player.mute();
                    if (pending) { island.load(pending.id, pending.t, pending.play); pending = null; }
                  },
                  onError: function (e) { post({ type: 'error', code: e.data }); }
                }
              });
            }

            window.island = {
              load: function (id, t, play) {
                if (!ready) { pending = { id: id, t: t, play: play }; return; }
                player.mute();
                if (play) player.loadVideoById({ videoId: id, startSeconds: t });
                else player.cueVideoById({ videoId: id, startSeconds: t });
              },
              play: function () { if (ready) { player.mute(); if (player.getPlayerState() !== 1) player.playVideo(); } },
              pause: function () { if (ready && player.getPlayerState() === 1) player.pauseVideo(); },
              seek: function (t) { if (ready) player.seekTo(t, true); },
              time: function () { return ready && player.getCurrentTime ? player.getCurrentTime() : -1; }
            };
          </script>
        </body>
        </html>
        """;
}
