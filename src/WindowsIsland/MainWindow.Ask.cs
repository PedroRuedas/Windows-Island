using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsIsland.Core;
using WindowsIsland.Interop;
using WindowsIsland.Services;

namespace WindowsIsland;

public partial class MainWindow
{
    // ── Ask Claude ──

    /// <summary>Global shortcut that opens the ask box from anywhere (first free combination wins).</summary>
    private void RegisterAskHotkey(IntPtr hwnd)
    {
        (uint Modifiers, string Label)[] options =
        [
            (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, "Ctrl+Alt+Espaço"),
            (NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, "Ctrl+Shift+Espaço"),
        ];
        foreach (var (modifiers, label) in options)
        {
            if (NativeMethods.RegisterHotKey(hwnd, AskHotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, NativeMethods.VK_SPACE))
            {
                _hotkeyLabel = label;
                return;
            }
        }
        App.Log("Atalho para perguntar ao Claude indisponível (as combinações já estão em uso por outro app).");
    }

    /// <summary>Opens the ask page with the cursor in the box, ready to type.</summary>
    private void OpenAsk()
    {
        if (_settings.Hidden || !IsVisible)
            return;
        _hoverDelay.Stop();
        _leaveDelay.Stop();
        _selectedKey = AskKey;
        _hoverExpanded = true;
        Refresh();
        EnableKeyboard();
    }

    /// <summary>Lets the island take keyboard focus for typing (it normally never activates).</summary>
    private void EnableKeyboard()
    {
        if (!_keyboard)
        {
            _keyboard = true;
            _previousForeground = NativeMethods.GetForegroundWindow();
            NativeMethods.SetNoActivate(new WindowInteropHelper(this).Handle, false);
            Activate();
        }
        AskInput.Focus();
        Keyboard.Focus(AskInput);
    }

    private void DisableKeyboard(bool restoreFocus)
    {
        if (!_keyboard)
            return;
        _keyboard = false;
        NativeMethods.SetNoActivate(new WindowInteropHelper(this).Handle, true);
        Keyboard.ClearFocus();
        if (restoreFocus && _previousForeground != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(_previousForeground);
        _previousForeground = IntPtr.Zero;
    }

    private void AskInput_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => EnableKeyboard();

    private void AskInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            // Enter sends; Shift+Enter breaks the line.
            e.Handled = true;
            SendAsk();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DisableKeyboard(restoreFocus: true);
            _hoverExpanded = false;
            Refresh();
        }
    }

    private void AskInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        AskPlaceholder.Visibility = AskInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // A longer question wraps onto more lines: grow the island with it.
        ResizeToCurrentView();
    }

    private void AskSend_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_chat.IsRunning)
            _chat.Cancel();
        else
            SendAsk();
    }

    private void AskNew_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _chat.NewConversation();
        if (_keyboard)
            AskInput.Focus();
    }

    private void SendAsk()
    {
        if (_chat.IsRunning || (string.IsNullOrWhiteSpace(AskInput.Text) && _askAttachments.Count == 0))
            return;
        _chat.Send(AskInput.Text, _askAttachments.ToList());
        AskInput.Clear();
        _askAttachments.Clear();
        UpdateAttachmentChips();
    }

    /// <summary>📷: attaches a screenshot of the window you were using before coming to the island.</summary>
    private void AskScreenshot_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var island = new WindowInteropHelper(this).Handle;
        var target = _keyboard ? _previousForeground : NativeMethods.GetForegroundWindow();
        if (target == island)
            target = IntPtr.Zero;
        if (ScreenCapture.Capture(target) is { } shot)
        {
            _askAttachments.Add(new ChatAttachment(shot.Path, IsImage: true) { Label = shot.Label });
            UpdateAttachmentChips();
        }
        EnableKeyboard();
    }

    private void AddAttachments(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (!_askAttachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                _askAttachments.Add(ChatAttachment.FromPath(path));
        UpdateAttachmentChips();
    }

    /// <summary>Chips above the field: a thumbnail or icon, the name, and ✕ to take it back out.</summary>
    private void UpdateAttachmentChips()
    {
        AskAttachments.Children.Clear();
        foreach (var attachment in _askAttachments.ToList())
        {
            var remove = new Button { Style = (Style)FindResource("FieldButton"), Content = "", FontSize = 9, Width = 20, Height = 20, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remover" };
            remove.Click += (_, e) =>
            {
                e.Handled = true;
                _askAttachments.Remove(attachment);
                UpdateAttachmentChips();
            };
            var chip = new Border
            {
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
                Padding = new Thickness(4, 4, 4, 4),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = attachment.Path,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new Border
                        {
                            Width = 26,
                            Height = 26,
                            CornerRadius = new CornerRadius(6),
                            Background = Shelf.Thumbnail(attachment.Path) is { } thumb ? new ImageBrush(thumb) { Stretch = Stretch.UniformToFill } : Brushes.Transparent,
                        },
                        new TextBlock
                        {
                            Text = attachment.Label ?? attachment.Name,
                            Foreground = Brushes.White,
                            FontSize = 12,
                            MaxWidth = 200,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Margin = new Thickness(8, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        remove,
                    },
                },
            };
            AskAttachments.Children.Add(chip);
        }
        AskAttachments.Visibility = _askAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResizeToCurrentView();
    }

    private void OnChatAnswered(string text)
    {
        if (_hoverExpanded && _shownPage?.Key == AskKey)
            return;
        string preview = text.Replace("**", "").Replace("`", "").ReplaceLineEndings(" ").Trim();
        _controller.Upsert(new IslandActivity
        {
            Id = "ask.answer",
            Caption = "Claude",
            Title = "Claude respondeu",
            Subtitle = preview.Length > 140 ? preview[..140] + "…" : preview,
            Icon = "claude",
            Accent = ClaudeService.Orange,
            Duration = TimeSpan.FromSeconds(8),
            Priority = 70,
            ExpandOnArrive = true,
            Source = AskKey,
        });
    }

    private void FillAsk()
    {
        bool running = _chat.IsRunning;
        var messages = _chat.Messages;
        AskStatus.Text = _chat.Status;
        AskStatus.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        AskNewButton.Visibility = messages.Count > 0 && !running ? Visibility.Visible : Visibility.Collapsed;
        AskHint.Visibility = messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AskHint.Text = "Tire dúvidas ou peça uma ajuda rápida. O Claude responde aqui mesmo, com o seu login do Claude Code."
            + (_hotkeyLabel.Length > 0 ? $" {_hotkeyLabel} abre esta caixa de qualquer lugar." : "");
        AskSendGlyph.Text = running ? "" : "";
        AskSendButton.ToolTip = running ? "Parar a resposta" : "Enviar (Enter)";
        SetSpin(AskHeaderSpin, running);

        string signature = $"{messages.Count}|{messages.LastOrDefault()?.Text.Length}|{running}";
        if (signature == _askSignature)
            return;
        _askSignature = signature;

        AskMessages.Children.Clear();
        for (int i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            bool last = i == messages.Count - 1;
            if (message.FromUser)
            {
                var bubble = new StackPanel();
                // Screenshots show as a small preview; other files by name.
                foreach (var attachment in message.Attachments)
                {
                    if (attachment.IsImage && Shelf.Thumbnail(attachment.Path) is { } preview)
                        bubble.Children.Add(new Border { Height = 90, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6), Background = new ImageBrush(preview) { Stretch = Stretch.UniformToFill } });
                    else
                        bubble.Children.Add(new TextBlock { Text = "📎 " + (attachment.Label ?? attachment.Name), Foreground = (Brush)FindResource("DimText"), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 4) });
                }
                bubble.Children.Add(new TextBlock { Text = message.Text, Foreground = Brushes.White, FontSize = 13, TextWrapping = TextWrapping.Wrap });
                AskMessages.Children.Add(new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    MaxWidth = 300,
                    MinWidth = message.Attachments.Any(a => a.IsImage) ? 180 : 0,
                    Margin = new Thickness(40, i == 0 ? 0 : 12, 0, 0),
                    Padding = new Thickness(12, 7, 12, 7),
                    CornerRadius = new CornerRadius(15),
                    Background = new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)),
                    Child = bubble,
                });
                continue;
            }

            var answer = new TextBlock
            {
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 13,
                LineHeight = 19,
                TextWrapping = TextWrapping.Wrap,
                Foreground = message.IsError ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)) : new SolidColorBrush(Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF)),
            };
            if (message.Text.Length == 0 && running && last)
                answer.Inlines.Add(new Run("…") { Foreground = (Brush)FindResource("DimText") });
            else
                AddFormatted(answer.Inlines, message.Text);
            AskMessages.Children.Add(answer);

            // The latest answer can be copied (TextBlocks aren't selectable).
            if (last && !running && !message.IsError && message.Text.Length > 0)
            {
                var copy = new Button { Style = (Style)FindResource("TextButton"), Content = "Copiar resposta", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-8, 4, 0, 0) };
                string text = message.Text;
                copy.Click += (_, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        Clipboard.SetText(text);
                        copy.Content = "Copiada ✓";
                    }
                    catch (Exception ex)
                    {
                        App.Log(ex);
                    }
                };
                AskMessages.Children.Add(copy);
            }
        }
        AskScroll.Visibility = messages.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // After layout, follow the newest text.
        Dispatcher.BeginInvoke(AskScroll.ScrollToEnd, DispatcherPriority.Loaded);
        ResizeToCurrentView();
    }

    /// <summary>Just enough Markdown for chat answers: **bold**, `code`, bullet lists and headings.</summary>
    private static void AddFormatted(InlineCollection inlines, string text)
    {
        var code = new FontFamily("Cascadia Mono, Consolas");
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                inlines.Add(new LineBreak());
            string line = lines[i];
            bool heading = false;
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                line = trimmed.TrimStart('#').TrimStart();
                heading = true;
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                line = new string(' ', line.Length - trimmed.Length) + "•  " + trimmed[2..];
            }

            foreach (var part in System.Text.RegularExpressions.Regex.Split(line, @"(\*\*[^*]+\*\*|`[^`]+`)"))
            {
                if (part.Length == 0)
                    continue;
                if (part.Length > 4 && part.StartsWith("**") && part.EndsWith("**"))
                    inlines.Add(new Run(part[2..^2]) { FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
                else if (part.Length > 2 && part[0] == '`' && part[^1] == '`')
                    inlines.Add(new Run(part[1..^1]) { FontFamily = code, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xB8, 0x80)) });
                else
                    inlines.Add(new Run(part) { FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal });
            }
        }
    }

    /// <summary>The ask page changes size as you type and as the answer streams in: spring the island to fit.</summary>
    private void ResizeToCurrentView()
    {
        if (_view != ViewKind.AskExpanded)
            return;
        _height.Target = MeasureHeight(AskView) + (_pagerVisible ? PagerHeight : 0);
        StartAnimation();
    }
}
