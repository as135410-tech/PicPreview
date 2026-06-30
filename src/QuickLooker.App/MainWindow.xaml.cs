using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QuickLooker.Core;

namespace QuickLooker.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly HashSet<string> NativePreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif",
        ".tif",
        ".tiff",
        ".ico"
    };

    private readonly ThumbnailCache _thumbnailCache = new();
    private readonly SemaphoreSlim _thumbnailLimiter = new(4, 4);
    private CancellationTokenSource? _folderCancellation;
    private CancellationTokenSource? _previewCancellation;
    private ImageSource? _currentPreview;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ImageItem> Images { get; } = new();

    public ImageSource? CurrentPreview
    {
        get => _currentPreview;
        private set
        {
            if (!ReferenceEquals(_currentPreview, value))
            {
                _currentPreview = value;
                OnPropertyChanged();
                EmptyText.Visibility = value is null ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择图片文件夹"
        };

        if (dialog.ShowDialog(this) == true)
        {
            await LoadFolderAsync(dialog.FolderName);
        }
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = SupportedImageFormats.FileDialogFilter
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenFileAsync(dialog.FileName);
        }
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        MoveSelection(-1);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        MoveSelection(1);
    }

    private async void ImageList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ImageList.SelectedItem is ImageItem item)
        {
            await LoadPreviewAsync(item);
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Left)
        {
            MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right || e.Key == Key.Space)
        {
            MoveSelection(1);
            e.Handled = true;
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var first = paths.FirstOrDefault();

        if (first is null)
        {
            return;
        }

        if (Directory.Exists(first))
        {
            await LoadFolderAsync(first);
        }
        else if (File.Exists(first) && SupportedImageFormats.IsSupported(first))
        {
            await OpenFileAsync(first);
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _folderCancellation?.Cancel();
        _previewCancellation?.Cancel();
        base.OnClosing(e);
    }

    private async Task OpenFileAsync(string filePath)
    {
        var folder = Path.GetDirectoryName(filePath);

        if (folder is null)
        {
            return;
        }

        await LoadFolderAsync(folder, Path.GetFullPath(filePath));
    }

    private async Task LoadFolderAsync(string folder, string? selectedPath = null)
    {
        _folderCancellation?.Cancel();
        _folderCancellation = new CancellationTokenSource();
        var token = _folderCancellation.Token;

        try
        {
            SetBusy(true);
            SetStatus("正在读取文件夹");
            FolderText.Text = folder;
            CurrentPreview = null;
            Images.Clear();

            var files = await Task.Run(
                () => SupportedImageFormats.EnumerateSupportedFiles(folder).ToList(),
                token);

            foreach (var file in files)
            {
                Images.Add(new ImageItem(file));
            }

            CountText.Text = files.Count == 0 ? "没有找到支持的图片" : $"{files.Count} 张图片";
            SetStatus(files.Count == 0 ? "没有找到支持的图片" : "正在生成缩略图");

            if (files.Count > 0)
            {
                var selected = selectedPath is null
                    ? Images[0]
                    : Images.FirstOrDefault(x => string.Equals(x.FullPath, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? Images[0];

                ImageList.SelectedItem = selected;
                ImageList.ScrollIntoView(selected);
                _ = StartThumbnailPumpAsync(Images.ToList(), token);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer folder load replaced this one.
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StartThumbnailPumpAsync(IReadOnlyList<ImageItem> items, CancellationToken token)
    {
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            await _thumbnailLimiter.WaitAsync(token);

            _ = Task.Run(async () =>
            {
                try
                {
                    var record = await _thumbnailCache.GetOrCreateAsync(item.FullPath, 160, token);
                    var thumbnail = LoadBitmapFromFile(record.ThumbnailPath);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        item.Thumbnail = thumbnail;
                        item.DetailText = $"{record.Width} x {record.Height}";
                    });
                }
                catch (OperationCanceledException)
                {
                    // Ignore canceled folder loads.
                }
                catch
                {
                    await Dispatcher.InvokeAsync(() => item.DetailText = "无法生成缩略图");
                }
                finally
                {
                    _thumbnailLimiter.Release();
                }
            }, token);
        }
    }

    private async Task LoadPreviewAsync(ImageItem item)
    {
        _previewCancellation?.Cancel();
        _previewCancellation = new CancellationTokenSource();
        var token = _previewCancellation.Token;

        try
        {
            SetBusy(true);
            SetStatus($"正在预览 {item.Name}");

            var extension = Path.GetExtension(item.FullPath);
            ImageSource preview;

            if (NativePreviewExtensions.Contains(extension))
            {
                try
                {
                    preview = await Task.Run(() => LoadBitmapFromFile(item.FullPath), token);
                }
                catch
                {
                    preview = await RenderPreviewAsync(item.FullPath, token);
                }
            }
            else
            {
                preview = await RenderPreviewAsync(item.FullPath, token);
            }

            if (!token.IsCancellationRequested)
            {
                CurrentPreview = preview;
                SetStatus(item.FullPath);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer image selection replaced this one.
        }
        catch (Exception ex)
        {
            CurrentPreview = null;
            SetStatus($"无法预览：{ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static async Task<ImageSource> RenderPreviewAsync(string path, CancellationToken token)
    {
        var bytes = await ImageRenderer.RenderPreviewPngAsync(path, 4096, token);
        return LoadBitmapFromBytes(bytes);
    }

    private static ImageSource LoadBitmapFromFile(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static ImageSource LoadBitmapFromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void MoveSelection(int delta)
    {
        if (Images.Count == 0)
        {
            return;
        }

        var index = ImageList.SelectedIndex;

        if (index < 0)
        {
            index = 0;
        }
        else
        {
            index = Math.Clamp(index + delta, 0, Images.Count - 1);
        }

        ImageList.SelectedIndex = index;
        ImageList.ScrollIntoView(Images[index]);
    }

    private void SetBusy(bool isBusy)
    {
        LoadingBadge.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
