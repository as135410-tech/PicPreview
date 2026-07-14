using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QuickLooker.Core;

namespace QuickLooker.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const double MinPreviewZoom = 0.5;
    private const double MaxPreviewZoom = 12.0;
    private const double PreviewZoomStep = 1.18;
    private const double ZoomSelectionTolerance = 0.001;

    private static readonly HashSet<string> NativePreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".tif",
        ".tiff",
        ".ico"
    };

    private readonly ThumbnailCache _thumbnailCache = new();
    private readonly SemaphoreSlim _thumbnailLimiter = new(4, 4);
    private CancellationTokenSource? _folderCancellation;
    private CancellationTokenSource? _previewCancellation;
    private DispatcherTimer? _gifTimer;
    private IReadOnlyList<GifPreviewFrame> _gifFrames = Array.Empty<GifPreviewFrame>();
    private int _gifFrameIndex;
    private int _gifCompletedIterations;
    private int _gifIterationCount;
    private ImageSource? _currentPreview;
    private double _previewZoom = 1.0;
    private int _previewRotationDegrees;
    private bool _isUpdatingZoomSelection;
    private bool _isPreviewDragging;
    private Point _previewDragStart;
    private Vector _previewDragStartOffset;

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
        private set => SetCurrentPreview(value, resetTransform: true);
    }

    public async Task OpenPathAsync(string path)
    {
        if (Directory.Exists(path))
        {
            await LoadFolderAsync(path);
        }
        else if (File.Exists(path) && SupportedImageFormats.IsSupported(path))
        {
            await OpenFileAsync(path);
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

    private void RotateLeft_Click(object sender, RoutedEventArgs e)
    {
        RotatePreview(-90);
    }

    private void RotateRight_Click(object sender, RoutedEventArgs e)
    {
        RotatePreview(90);
    }

    private void ZoomComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingZoomSelection || CurrentPreview is null)
        {
            return;
        }

        if (ZoomComboBox.SelectedItem is ComboBoxItem item && TryGetZoomLevel(item, out var zoom))
        {
            SetPreviewZoom(zoom, GetPreviewCenter(), resetOffset: Math.Abs(zoom - 1.0) < ZoomSelectionTolerance);
        }
    }

    private void PreviewViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CurrentPreview is null)
        {
            return;
        }

        var nextZoom = e.Delta > 0
            ? _previewZoom * PreviewZoomStep
            : _previewZoom / PreviewZoomStep;

        SetPreviewZoom(nextZoom, e.GetPosition(PreviewViewport), resetOffset: false);
        e.Handled = true;
    }

    private void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdatePreviewRotationCenter();
    }

    private void PreviewViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (CurrentPreview is null)
        {
            return;
        }

        _isPreviewDragging = true;
        _previewDragStart = e.GetPosition(PreviewViewport);
        _previewDragStartOffset = new Vector(PreviewTranslateTransform.X, PreviewTranslateTransform.Y);
        PreviewViewport.CaptureMouse();
        PreviewViewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void PreviewViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPreviewDragging)
        {
            return;
        }

        var position = e.GetPosition(PreviewViewport);
        PreviewTranslateTransform.X = _previewDragStartOffset.X + position.X - _previewDragStart.X;
        PreviewTranslateTransform.Y = _previewDragStartOffset.Y + position.Y - _previewDragStart.Y;
        e.Handled = true;
    }

    private void PreviewViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopPreviewDrag();
        e.Handled = true;
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
        StopGifAnimation();
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
            StopGifAnimation();
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
        StopGifAnimation();
        _previewCancellation = new CancellationTokenSource();
        var token = _previewCancellation.Token;

        try
        {
            SetBusy(true);
            SetStatus($"正在预览 {item.Name}");

            var extension = Path.GetExtension(item.FullPath);

            if (string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase))
            {
                var animation = await RenderGifPreviewAsync(item.FullPath, token);

                if (!token.IsCancellationRequested)
                {
                    StartGifAnimation(animation);
                    SetStatus($"{item.FullPath} · GIF 动画 · {animation.Frames.Count} 帧");
                }

                return;
            }

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

    private static async Task<GifPreviewAnimation> RenderGifPreviewAsync(string path, CancellationToken token)
    {
        var rendered = await ImageRenderer.RenderGifAnimationAsync(path, 4096, token).ConfigureAwait(false);
        var frames = new GifPreviewFrame[rendered.Frames.Count];

        for (var index = 0; index < rendered.Frames.Count; index++)
        {
            token.ThrowIfCancellationRequested();

            var frame = rendered.Frames[index];
            frames[index] = new GifPreviewFrame(LoadBitmapFromBytes(frame.PngBytes), frame.Delay);
        }

        return new GifPreviewAnimation(frames, rendered.IterationCount);
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

    private void StartGifAnimation(GifPreviewAnimation animation)
    {
        StopGifAnimation();

        if (animation.Frames.Count == 0)
        {
            throw new InvalidDataException("GIF 中没有可显示的帧。");
        }

        _gifFrames = animation.Frames;
        _gifIterationCount = animation.IterationCount;
        _gifFrameIndex = 0;
        _gifCompletedIterations = 0;
        SetCurrentPreview(_gifFrames[0].Image, resetTransform: true);

        if (_gifFrames.Count == 1)
        {
            return;
        }

        _gifTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = _gifFrames[0].Delay
        };
        _gifTimer.Tick += GifTimer_Tick;
        _gifTimer.Start();
    }

    private void GifTimer_Tick(object? sender, EventArgs e)
    {
        var nextFrameIndex = _gifFrameIndex + 1;

        if (nextFrameIndex >= _gifFrames.Count)
        {
            _gifCompletedIterations++;

            if (_gifIterationCount > 0 && _gifCompletedIterations >= _gifIterationCount)
            {
                _gifTimer?.Stop();
                return;
            }

            nextFrameIndex = 0;
        }

        _gifFrameIndex = nextFrameIndex;
        SetCurrentPreview(_gifFrames[_gifFrameIndex].Image, resetTransform: false);

        if (_gifTimer is not null)
        {
            _gifTimer.Interval = _gifFrames[_gifFrameIndex].Delay;
        }
    }

    private void StopGifAnimation()
    {
        if (_gifTimer is not null)
        {
            _gifTimer.Stop();
            _gifTimer.Tick -= GifTimer_Tick;
            _gifTimer = null;
        }

        _gifFrames = Array.Empty<GifPreviewFrame>();
        _gifFrameIndex = 0;
        _gifCompletedIterations = 0;
        _gifIterationCount = 0;
    }

    private void SetCurrentPreview(ImageSource? value, bool resetTransform)
    {
        if (ReferenceEquals(_currentPreview, value))
        {
            return;
        }

        _currentPreview = value;
        OnPropertyChanged(nameof(CurrentPreview));
        EmptyText.Visibility = value is null ? Visibility.Visible : Visibility.Collapsed;

        if (resetTransform)
        {
            ResetPreviewTransform();
        }
    }

    private void StopPreviewDrag()
    {
        if (!_isPreviewDragging)
        {
            return;
        }

        _isPreviewDragging = false;
        PreviewViewport.ReleaseMouseCapture();
        PreviewViewport.Cursor = null;
    }

    private void RotatePreview(int deltaDegrees)
    {
        if (CurrentPreview is null)
        {
            return;
        }

        _previewRotationDegrees = NormalizeRotation(_previewRotationDegrees + deltaDegrees);
        UpdatePreviewRotationCenter();
        PreviewRotateTransform.Angle = _previewRotationDegrees;
    }

    private void SetPreviewZoom(double zoom, Point anchor, bool resetOffset)
    {
        var oldZoom = _previewZoom;
        var nextZoom = Math.Clamp(zoom, MinPreviewZoom, MaxPreviewZoom);

        if (Math.Abs(nextZoom - oldZoom) < ZoomSelectionTolerance)
        {
            if (resetOffset)
            {
                ResetPreviewOffset();
            }

            UpdateZoomComboBoxText();
            return;
        }

        _previewZoom = nextZoom;

        var scale = _previewZoom / oldZoom;
        PreviewTranslateTransform.X = anchor.X - scale * (anchor.X - PreviewTranslateTransform.X);
        PreviewTranslateTransform.Y = anchor.Y - scale * (anchor.Y - PreviewTranslateTransform.Y);
        PreviewScaleTransform.ScaleX = _previewZoom;
        PreviewScaleTransform.ScaleY = _previewZoom;

        if (resetOffset)
        {
            ResetPreviewOffset();
        }

        UpdateZoomComboBoxText();
    }

    private void ResetPreviewOffset()
    {
        PreviewTranslateTransform.X = 0;
        PreviewTranslateTransform.Y = 0;
    }

    private Point GetPreviewCenter()
    {
        return new Point(PreviewViewport.ActualWidth / 2.0, PreviewViewport.ActualHeight / 2.0);
    }

    private void UpdatePreviewRotationCenter()
    {
        if (PreviewRotateTransform is null)
        {
            return;
        }

        var width = PreviewImage?.ActualWidth > 0 ? PreviewImage.ActualWidth : PreviewViewport.ActualWidth;
        var height = PreviewImage?.ActualHeight > 0 ? PreviewImage.ActualHeight : PreviewViewport.ActualHeight;

        PreviewRotateTransform.CenterX = width / 2.0;
        PreviewRotateTransform.CenterY = height / 2.0;
    }

    private static int NormalizeRotation(int degrees)
    {
        degrees %= 360;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static bool TryGetZoomLevel(ComboBoxItem item, out double zoom)
    {
        zoom = 1.0;

        return item.Tag is not null
            && double.TryParse(item.Tag.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out zoom);
    }

    private void UpdateZoomComboBoxText()
    {
        if (ZoomComboBox is null)
        {
            return;
        }

        _isUpdatingZoomSelection = true;

        try
        {
            foreach (var item in ZoomComboBox.Items.OfType<ComboBoxItem>())
            {
                if (TryGetZoomLevel(item, out var itemZoom)
                    && Math.Abs(itemZoom - _previewZoom) < ZoomSelectionTolerance)
                {
                    ZoomComboBox.SelectedItem = item;
                    return;
                }
            }

            ZoomComboBox.SelectedIndex = -1;
            ZoomComboBox.Text = string.Create(CultureInfo.InvariantCulture, $"{_previewZoom * 100.0:0}%");
        }
        finally
        {
            _isUpdatingZoomSelection = false;
        }
    }

    private void ResetPreviewTransform()
    {
        _previewZoom = 1.0;
        _previewRotationDegrees = 0;
        _isPreviewDragging = false;

        if (PreviewScaleTransform is not null)
        {
            PreviewScaleTransform.ScaleX = 1;
            PreviewScaleTransform.ScaleY = 1;
        }

        if (PreviewTranslateTransform is not null)
        {
            ResetPreviewOffset();
        }

        if (PreviewRotateTransform is not null)
        {
            UpdatePreviewRotationCenter();
            PreviewRotateTransform.Angle = 0;
        }

        if (PreviewViewport is not null)
        {
            PreviewViewport.ReleaseMouseCapture();
            PreviewViewport.Cursor = null;
        }

        UpdateZoomComboBoxText();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
