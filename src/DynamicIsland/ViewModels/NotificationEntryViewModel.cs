using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland.Core.Models;
using DynamicIsland.Core.Services;
using DynamicIsland.Infrastructure;

namespace DynamicIsland.ViewModels;

/// <summary>通知列表 / 通知卡里的一条通知：负责解析来源应用图标并支持点击唤起。</summary>
public sealed class NotificationEntryViewModel : ObservableObject
{
    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private ImageSource? _icon;
    private bool _iconLoaded;

    public NotificationEntryViewModel(NotificationItem item)
    {
        Item = item;
        OpenCommand = new RelayCommand(_ => Activate());
    }

    public NotificationItem Item { get; }

    public RelayCommand OpenCommand { get; }

    public string AppName => string.IsNullOrWhiteSpace(Item.AppName) ? "通知" : Item.AppName;

    public string DisplayTitle => Item.DisplayTitle;

    public string Body => Item.Body;

    public string TimeText
    {
        get
        {
            DateTime local = Item.ReceivedAt.LocalDateTime;
            DateTime now = DateTime.Now;
            if (local.Date == now.Date)
            {
                return local.ToString("HH:mm");
            }

            if (local.Date == now.Date.AddDays(-1))
            {
                return "昨天";
            }

            return local.ToString("M/d");
        }
    }

    /// <summary>没有真实图标时显示的应用首字。</summary>
    public string Initial
    {
        get
        {
            string name = AppName.Trim();
            if (name.Length == 0)
            {
                return "?";
            }

            // 中文取第一个字；英文取首字母大写
            char first = name[0];
            return char.IsLetter(first) ? char.ToUpperInvariant(first).ToString() : first.ToString();
        }
    }

    /// <summary>占位图标底色：按应用名稳定取色。</summary>
    public Brush PlaceholderBrush
    {
        get
        {
            string[] palette =
            {
                "#FF4C6FFF", "#FF2FAE8F", "#FFD9772E", "#FF9A5BD6",
                "#FF3C9BD8", "#FFD45C7A", "#FF6B8E23", "#FF7A6CFF"
            };

            int hash = 17;
            foreach (char c in AppName)
            {
                hash = (hash * 31 + c) & 0x7FFFFFFF;
            }

            Color color = (Color)ColorConverter.ConvertFromString(palette[hash % palette.Length]);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    public bool HasIcon => Icon is not null;

    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded)
            {
                _iconLoaded = true;
                _icon = LoadIcon(Item.AppId);
                OnPropertyChanged(nameof(HasIcon));
            }

            return _icon;
        }
    }

    private static ImageSource? LoadIcon(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        lock (IconCache)
        {
            if (IconCache.TryGetValue(appId, out ImageSource? cached))
            {
                return cached;
            }
        }

        ImageSource? image = null;
        try
        {
            var raw = NotificationApps.ResolveIcon(appId, 64);
            if (raw is { Width: > 0, Height: > 0 })
            {
                var source = BitmapSource.Create(
                    raw.Width,
                    raw.Height,
                    64,
                    64,
                    PixelFormats.Bgra32,
                    null,
                    raw.Bgra,
                    raw.Width * 4);
                source.Freeze();
                image = source;
            }
        }
        catch
        {
            image = null;
        }

        lock (IconCache)
        {
            IconCache[appId] = image;
        }

        return image;
    }

    private void Activate()
    {
        if (!NotificationApps.TryActivate(Item.AppId))
        {
            LogService.Warn($"通知来源无法启动：{Item.AppName}（{Item.AppId}）");
        }
    }
}
