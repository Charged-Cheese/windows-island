using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Core.Models;
using DynamicIsland.Core.Services;
using DynamicIsland.Infrastructure;

namespace DynamicIsland.ViewModels;

public sealed class LauncherEntryViewModel : ObservableObject
{
    private ImageSource? _icon;
    private bool _iconLoaded;

    public LauncherEntryViewModel(LauncherItem item, LauncherService service)
    {
        Item = item;
        Service = service;
        Icon = null;
    }

    public LauncherItem Item { get; }

    public LauncherService Service { get; }

    public string Name => string.IsNullOrWhiteSpace(Item.Name) ? "未命名" : Item.Name;

    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = LoadIcon(Item);
            }

            return _icon;
        }
        private set => SetProperty(ref _icon, value);
    }

    private static ImageSource? LoadIcon(LauncherItem item)
    {
        try
        {
            string path = File.Exists(item.SourcePath) ? item.SourcePath : item.TargetPath;
            var raw = LauncherService.GetIcon(path, 96);
            if (raw is null)
            {
                return null;
            }

            var source = BitmapSource.Create(
                raw.Width,
                raw.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                raw.Bgra,
                raw.Width * 4);

            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }
}
