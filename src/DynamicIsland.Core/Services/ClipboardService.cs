namespace DynamicIsland.Core.Services;

public enum ClipboardKind
{
    Text,
    Image
}

/// <summary>一条剪贴板记录（图片以 PNG 字节保存，避免 Core 依赖 WPF 图像类型）。</summary>
public sealed record ClipboardEntry(ClipboardKind Kind, string Text, byte[]? ImagePng, DateTimeOffset Time)
{
    public string Preview => Kind == ClipboardKind.Text
        ? (Text.Length > 120 ? Text[..120] + "…" : Text)
        : "图片";

    public string Meta => Kind == ClipboardKind.Text
        ? $"{Text.Length} 个字符"
        : (ImagePng?.Length ?? 0) >= 1024
            ? $"{ImagePng!.Length / 1024} KB"
            : $"{ImagePng?.Length ?? 0} B";
}

/// <summary>剪贴板历史（最近的文本 / 图片，仅存内存）。</summary>
public sealed class ClipboardService
{
    public const int Capacity = 20;

    private readonly List<ClipboardEntry> _items = new();
    private readonly object _gate = new();

    public IReadOnlyList<ClipboardEntry> Items
    {
        get
        {
            lock (_gate)
            {
                return _items.ToList();
            }
        }
    }

    /// <summary>自己写回剪贴板时置位，避免把回填当成新内容再抓一次。</summary>
    public bool IgnoreNext { get; set; }

    public event Action<ClipboardEntry>? ItemAdded;

    public event Action? Changed;

    public void AddText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Add(new ClipboardEntry(ClipboardKind.Text, text, null, DateTimeOffset.Now));
    }

    public void AddImage(byte[] png)
    {
        if (png.Length == 0)
        {
            return;
        }

        Add(new ClipboardEntry(ClipboardKind.Image, string.Empty, png, DateTimeOffset.Now));
    }

    private void Add(ClipboardEntry entry)
    {
        lock (_gate)
        {
            // 连续复制同一段文字 / 同一张图片时只保留一条
            if (_items.Count > 0)
            {
                ClipboardEntry latest = _items[0];
                if (entry.Kind == ClipboardKind.Text &&
                    latest.Kind == ClipboardKind.Text &&
                    string.Equals(latest.Text, entry.Text, StringComparison.Ordinal))
                {
                    return;
                }

                if (entry.Kind == ClipboardKind.Image &&
                    latest.Kind == ClipboardKind.Image &&
                    latest.ImagePng is { } previous &&
                    entry.ImagePng is { } current &&
                    previous.AsSpan().SequenceEqual(current))
                {
                    return;
                }
            }

            _items.Insert(0, entry);
            while (_items.Count > Capacity)
            {
                _items.RemoveAt(_items.Count - 1);
            }
        }

        ItemAdded?.Invoke(entry);
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }

        Changed?.Invoke();
    }
}
