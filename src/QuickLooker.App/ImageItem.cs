using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace QuickLooker.App;

public sealed class ImageItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private string _detailText = "等待缩略图";

    public ImageItem(string path)
    {
        FullPath = path;
        Name = Path.GetFileName(path);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FullPath { get; }

    public string Name { get; }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!ReferenceEquals(_thumbnail, value))
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }
    }

    public string DetailText
    {
        get => _detailText;
        set
        {
            if (_detailText != value)
            {
                _detailText = value;
                OnPropertyChanged();
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
