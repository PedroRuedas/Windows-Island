using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WindowsIsland.Core;

namespace WindowsIsland;

public partial class MainWindow
{
    // ── Shelf ──

    private void FillShelf()
    {
        _shelf.Prune();
        var items = _shelf.Items;
        ShelfHint.Text = items.Count == 0
            ? "Solte aqui os arquivos que você quer guardar por um momento. Depois é só arrastá-los para onde quiser."
            : "Arraste para usar • duplo clique abre • 💬 pergunta ao Claude sobre o arquivo";
        ShelfClearButton.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        string signature = string.Join("|", items);
        if (signature == _shelfSignature)
            return;
        _shelfSignature = signature;
        ShelfItems.Children.Clear();
        foreach (var path in items)
            ShelfItems.Children.Add(BuildShelfTile(path));
    }

    private FrameworkElement BuildShelfTile(string path)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\'));
        var icon = Shelf.Thumbnail(path);
        bool picture = icon is System.Windows.Media.Imaging.BitmapImage;

        var remove = new Button { Style = (Style)FindResource("FieldButton"), Content = "", FontSize = 9, Width = 22, Height = 22, ToolTip = "Tirar da prateleira", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden };
        remove.Click += (_, e) =>
        {
            e.Handled = true;
            _shelf.Remove(path);
        };
        var ask = new Button { Style = (Style)FindResource("FieldButton"), Content = "", FontSize = 11, Width = 22, Height = 22, ToolTip = "Perguntar ao Claude sobre isto", HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Hidden };
        ask.Click += (_, e) =>
        {
            e.Handled = true;
            AddAttachments([path]);
            SelectPage(AskKey);
            EnableKeyboard();
        };

        var tile = new Border
        {
            Width = 84,
            Height = 92,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = path,
            Child = new Grid
            {
                Children =
                {
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new Border
                            {
                                Width = picture ? 56 : 40,
                                Height = picture ? 42 : 40,
                                CornerRadius = new CornerRadius(picture ? 6 : 0),
                                Background = icon is null ? Brushes.Transparent : new ImageBrush(icon) { Stretch = picture ? Stretch.UniformToFill : Stretch.Uniform },
                            },
                            new TextBlock
                            {
                                Text = name,
                                Foreground = Brushes.White,
                                FontSize = 11,
                                TextAlignment = TextAlignment.Center,
                                TextWrapping = TextWrapping.Wrap,
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                MaxHeight = 30,
                                Margin = new Thickness(6, 6, 6, 0),
                            },
                        },
                    },
                    ask,
                    remove,
                },
            },
        };

        var hover = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
        tile.MouseEnter += (_, _) =>
        {
            tile.Background = hover;
            remove.Visibility = ask.Visibility = Visibility.Visible;
        };
        tile.MouseLeave += (_, _) =>
        {
            tile.Background = Brushes.Transparent;
            remove.Visibility = ask.Visibility = Visibility.Hidden;
        };

        // Drag it out to any app (Explorer, WhatsApp, an e-mail…); double-click opens it.
        Point? pressedAt = null;
        tile.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                OpenUrl(path);
                return;
            }
            pressedAt = e.GetPosition(tile);
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            pressedAt = null;
        };
        tile.MouseMove += (_, e) =>
        {
            if (pressedAt is not { } start || e.LeftButton != MouseButtonState.Pressed)
                return;
            var delta = e.GetPosition(tile) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            pressedAt = null;
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            try
            {
                DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            }
            catch (Exception ex)
            {
                App.Log(ex);
            }
        };
        return tile;
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _shelf.Clear();
    }

    // ── Dropping files on the island ──

    private static string[]? DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    /// <summary>Like the notch apps on the Mac: drag files over the island and it opens to take them.</summary>
    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        if (DroppedFiles(e) is null)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        _leaveDelay.Stop();
        _hoverDelay.Stop();
        if (!_dragging)
        {
            _dragging = true;
            // Over an open ask page the files become attachments; anywhere else they go on the shelf.
            if (!(_hoverExpanded && _shownPage?.Key == AskKey))
                _selectedKey = ShelfKey;
            _hoverExpanded = true;
            Refresh();
        }
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Island_DragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        // DragLeave also fires when moving between children: only end once the cursor really left.
        Dispatcher.BeginInvoke(() =>
        {
            if (IsCursorOverIsland())
                return;
            _dragging = false;
            _leaveDelay.Start();
        }, DispatcherPriority.Background);
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragging = false;
        if (DroppedFiles(e) is not { Length: > 0 } files)
            return;
        if (_shownPage?.Key == AskKey)
        {
            AddAttachments(files);
            Refresh();
            return;
        }
        _shelf.Add(files);
        SelectPage(ShelfKey);
    }

    /// <summary>When the answer lands while you're elsewhere, the island tells you (click it to read).</summary>
}
