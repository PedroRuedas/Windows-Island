using System.Windows;
using WindowsIsland.Controls;
using WindowsIsland.Core;
using WindowsIsland.Services;

namespace WindowsIsland;

public partial class MainWindow
{
    /// <summary>The YouTube video the browser is playing, once identified (null while unknown or not YouTube).</summary>
    private YouTubeVideo? CurrentVideo(MediaInfo media)
    {
        if (!YouTubeResolver.IsBrowser(media.SourceApp))
            return null;
        if (_youtube.TryGet(media, out var video))
            return video is not null && !_unembeddable.Contains(video.VideoId) ? video : null;
        _youtube.Resolve(media, Refresh);
        return null;
    }

    /// <summary>
    /// Plays the mirror while the player page is open; pauses it otherwise so it costs nothing.
    /// While the video is pinned, the pinned window plays it instead.
    /// </summary>
    private void UpdateVideoMirror(ViewKind kind, MediaInfo? media)
    {
        if (_pip is null && media is not null && CurrentVideo(media) is { } video && kind == ViewKind.MediaExpanded)
        {
            if (_mirror is null)
            {
                _mirror = new YouTubeMirror();
                _mirror.Failed += id =>
                {
                    _unembeddable.Add(id);
                    Refresh();
                };
                VideoHost.Child = _mirror.View;
            }
            _videoShown = true;
            // Called on every refresh (≈1/s): the first call loads the video, later ones keep it in sync.
            _ = _mirror.ShowAsync(video.VideoId, media.CurrentPosition, media.IsPlaying);
        }
        else if (_videoShown)
        {
            _videoShown = false;
            _ = _mirror?.PauseAsync();
        }
    }

    // ── Pinned video (picture-in-picture) ──

    private void PinVideo_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        PinVideo();
    }

    private void UnpinVideo_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        UnpinVideo(returnToIsland: true);
    }

    /// <summary>Moves the YouTube video out of the island into a small window that stays on top and can be dragged anywhere.</summary>
    private void PinVideo()
    {
        if (_pip is not null)
            return;
        var mirror = new YouTubeMirror();
        mirror.Failed += id =>
        {
            _unembeddable.Add(id);
            Refresh();
        };
        _pip = new VideoPipWindow(_settings, mirror);
        _pip.PlayPauseRequested += async () => await RunMedia(_media.TogglePlayPauseAsync);
        _pip.PreviousRequested += async () => await RunMedia(_media.PreviousAsync);
        _pip.NextRequested += async () => await RunMedia(_media.NextAsync);
        _pip.SeekRequested += async fraction =>
        {
            if (_controller.Media is { Duration.TotalSeconds: > 0 } media)
                await RunMedia(() => _media.SeekAsync(media.Duration * fraction));
        };
        _pip.CloseRequested += () => UnpinVideo(returnToIsland: false);
        _pip.ReturnRequested += () => UnpinVideo(returnToIsland: true);
        _pipMediaLostSince = null;
        _pip.Show();

        // The island doesn't need to stay open: the video now lives in its own window.
        _hoverExpanded = false;
        _hoverDelay.Stop();
        _leaveDelay.Stop();
        Refresh();
    }

    /// <summary>Closes the pinned window; when going back to the island, shows the player there for a moment.</summary>
    private void UnpinVideo(bool returnToIsland)
    {
        if (_pip is not { } pip)
            return;
        _pip = null;
        pip.CloseAnimated();
        if (returnToIsland && FindPage("media") is not null)
        {
            _selectedKey = "media";
            _peekingMedia = true;
            _mediaPeekTimer.Stop();
            _mediaPeekTimer.Start();
        }
        Refresh();
    }

    /// <summary>
    /// Keeps the pinned window on the browser's video, like the island's own player. Runs even while the island
    /// is hidden. Closes the window once the browser's media session has been gone for a few seconds.
    /// </summary>
    private void UpdatePip()
    {
        if (_pip is null)
            return;
        var media = _controller.Media;
        if (media is null || !YouTubeResolver.IsBrowser(media.SourceApp))
        {
            _pipMediaLostSince ??= DateTime.Now;
            if (DateTime.Now - _pipMediaLostSince > TimeSpan.FromSeconds(5))
            {
                UnpinVideo(returnToIsland: false);
                return;
            }
            _pip.Update(null, hasVideo: false);
            _ = _pip.Mirror.PauseAsync();
            return;
        }

        _pipMediaLostSince = null;
        // Ads and videos that refuse embedding have no mirror: the window shows the artwork until a video is back.
        var video = CurrentVideo(media);
        _pip.Update(media, video is not null);
        if (video is not null)
            _ = _pip.Mirror.ShowAsync(video.VideoId, media.CurrentPosition, media.IsPlaying);
        else
            _ = _pip.Mirror.PauseAsync();
    }
}
