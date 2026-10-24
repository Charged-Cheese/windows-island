using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Core.Services;

namespace DynamicIsland.ViewModels;

/// <summary>剪贴板历史里的一条记录（文本 / 图片缩略图）。</summary>
public sealed class ClipboardEntryViewModel
{
    private ImageSource? _thumbnail;
    private bool _thumbnailLoaded;

    public ClipboardEntryViewModel(ClipboardEntry entry)
    {
        Entry = entry;
    }

    public ClipboardEntry Entry { get; }

    public bool IsImage => Entry.Kind == ClipboardKind.Image;

    public bool IsText => !IsImage;

    public string Preview => Entry.Preview;

    public string Meta => Entry.Meta;

    public string TimeText => Entry.Time.ToString("HH:mm");

    public ImageSource? Thumbnail
    {
        get
        {
            if (!_thumbnailLoaded)
            {
                _thumbnailLoaded = true;
                _thumbnail = LoadThumbnail(Entry.ImagePng);
            }

            return _thumbnail;
        }
    }

    private static ImageSource? LoadThumbnail(byte[]? png)
    {
        if (png is not { Length: > 0 })
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
