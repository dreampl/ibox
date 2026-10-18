using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

namespace JackiVpn.Desktop.Services;

/// <summary>What a shelf item is (its placeholder and icon on the notch).</summary>
public enum ShelfKind
{
    File = 0,
    Folder,
    App,
    Image,
    Video,
    Audio,
    Document,
    Archive,
    Text,
    Link,
    Many,
}

/// <summary>One thing kept on the notch's shelf.</summary>
public sealed record ShelfItem
{
    public required string Id { get; init; }

    public ShelfKind Kind { get; init; }

    public required string Name { get; init; }

    /// <summary>The file or folder (dropped files stay where they are; text, links and pictures are saved as files).</summary>
    public required string Path { get; init; }

    /// <summary>Text and links: the words themselves (to drop into an editor or a browser).</summary>
    public string? Text { get; init; }

    /// <summary>A file the shelf made itself (deleted with the item).</summary>
    public bool Owned { get; init; }

    public DateTimeOffset Added { get; init; } = DateTimeOffset.UtcNow;

    public bool Exists => Kind == ShelfKind.Folder ? Directory.Exists(Path) : File.Exists(Path);
}

/// <summary>
/// The notch's shelf: drop files, folders, apps, text, links or pictures on the notch and they wait there; drag them
/// out again to copy them wherever they are dropped (the shelf keeps them until removed). Dropped files and folders
/// are kept by reference — nothing large is copied — while text, links and pictures become small files of their own.
/// Saved with the user's data, so the shelf survives a restart.
/// </summary>
public sealed class NotchShelf
{
    public const int MaxItems = 24;

    /// <summary>Marks drags that come from the shelf itself (dropping them back on the notch does nothing).</summary>
    public const string OwnFormat = "IBOX.ShelfItem";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string _folder;
    private readonly string _index;
    private List<ShelfItem> _items = [];

    public NotchShelf(string folder)
    {
        _folder = folder;
        _index = System.IO.Path.Combine(folder, "items.json");
        Load();
    }

    public event EventHandler? Changed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ShelfItem> Items => _items;

    public int Count => _items.Count;

    /// <summary>What a drag carries, for the placeholder (null = nothing the shelf takes).</summary>
    public static (ShelfKind Kind, int Count, string Label)? Peek(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.GetDataPresent(OwnFormat))
        {
            return null;
        }

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            if (files.Length > 1)
            {
                return (ShelfKind.Many, files.Length, files.Length + " items");
            }

            var kind = KindOf(files[0]);
            return (kind, 1, System.IO.Path.GetFileName(files[0].TrimEnd('\\', '/')) is { Length: > 0 } n ? n : files[0]);
        }

        if (Url(data) is { } url)
        {
            return (ShelfKind.Link, 1, Host(url));
        }

        if (data.GetDataPresent(DataFormats.Bitmap))
        {
            return (ShelfKind.Image, 1, "Picture");
        }

        if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
        {
            return (ShelfKind.Text, 1, "Text");
        }

        return null;
    }

    /// <summary>Takes what was dropped; returns the new items (empty when nothing could be kept).</summary>
    public IReadOnlyList<ShelfItem> Add(IDataObject data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var added = new List<ShelfItem>();
        if (data.GetDataPresent(OwnFormat))
        {
            return added;
        }

        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            foreach (var path in files.Where(p => File.Exists(p) || Directory.Exists(p)).Take(MaxItems))
            {
                // The same file dropped again moves to the front instead of appearing twice.
                _items.RemoveAll(i => !i.Owned && string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase));
                added.Add(new ShelfItem
                {
                    Id = NewId(),
                    Kind = KindOf(path),
                    Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path,
                    Path = path,
                });
            }
        }
        else if (Url(data) is { } url)
        {
            var id = NewId();
            var file = Save(id, Safe(Host(url)) + ".url", Encoding.UTF8.GetBytes("[InternetShortcut]\r\nURL=" + url + "\r\n"));
            added.Add(new ShelfItem { Id = id, Kind = ShelfKind.Link, Name = Host(url), Path = file, Text = url, Owned = true });
        }
        else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            var id = NewId();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream();
            encoder.Save(png);
            var file = Save(id, "Picture " + DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture) + ".png", png.ToArray());
            added.Add(new ShelfItem { Id = id, Kind = ShelfKind.Image, Name = System.IO.Path.GetFileName(file), Path = file, Owned = true });
        }
        else if ((data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text)) is string { Length: > 0 } text)
        {
            var id = NewId();
            var title = FirstWords(text);
            var file = Save(id, Safe(title) + ".txt", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray());
            added.Add(new ShelfItem { Id = id, Kind = ShelfKind.Text, Name = title, Path = file, Text = text.Length > 200_000 ? null : text, Owned = true });
        }

        if (added.Count == 0)
        {
            return added;
        }

        _items.InsertRange(0, added);
        foreach (var old in _items.Skip(MaxItems).ToList())
        {
            Delete(old);
        }

        _items = _items.Take(MaxItems).ToList();
        Persist();
        return added;
    }

    public void Remove(string id)
    {
        if (_items.Find(i => i.Id == id) is { } item)
        {
            _items.Remove(item);
            Delete(item);
            Persist();
        }
    }

    public void Clear()
    {
        foreach (var item in _items)
        {
            Delete(item);
        }

        _items.Clear();
        Persist();
    }

    /// <summary>What a drag out of the shelf carries: the file (copied where it is dropped), plus the text or link itself.</summary>
    public static DataObject DataFor(ShelfItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var data = new DataObject();
        data.SetData(OwnFormat, item.Id);
        if (item.Exists)
        {
            var files = new StringCollection { item.Path };
            data.SetFileDropList(files);

            // Explorer copies (never moves) what comes from the shelf.
            data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1 /* DROPEFFECT_COPY */)));
        }

        if (item.Kind == ShelfKind.Link && item.Text is { } url)
        {
            data.SetData(DataFormats.UnicodeText, url);
            data.SetData("UniformResourceLocatorW", new MemoryStream(Encoding.Unicode.GetBytes(url + "\0")));
        }
        else if (item.Kind == ShelfKind.Text && item.Text is { } text)
        {
            data.SetData(DataFormats.UnicodeText, text);
        }

        return data;
    }

    public static ShelfKind KindOf(string path)
    {
        if (Directory.Exists(path))
        {
            return ShelfKind.Folder;
        }

        return System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".exe" or ".lnk" or ".msi" or ".appref-ms" or ".bat" or ".cmd" => ShelfKind.App,
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".heic" or ".svg" or ".ico" or ".tif" or ".tiff" => ShelfKind.Image,
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".wmv" or ".m4v" => ShelfKind.Video,
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".aac" or ".wma" or ".opus" => ShelfKind.Audio,
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".xz" or ".bz2" or ".iso" or ".cab" => ShelfKind.Archive,
            ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".md" or ".rtf" or ".odt" or ".csv" or ".json" or ".xml" or ".html" or ".htm" => ShelfKind.Document,
            ".url" => ShelfKind.Link,
            _ => ShelfKind.File,
        };
    }

    /// <summary>The Segoe MDL2 glyph for a kind (placeholders, and when the shell has no picture).</summary>
    public static string Glyph(ShelfKind kind) => kind switch
    {
        ShelfKind.Folder => "",
        ShelfKind.App => "",
        ShelfKind.Image => "",
        ShelfKind.Video => "",
        ShelfKind.Audio => "",
        ShelfKind.Archive => "",
        ShelfKind.Document => "",
        ShelfKind.Text => "",
        ShelfKind.Link => "",
        ShelfKind.Many => "",
        _ => "",
    };

    private static string? Url(IDataObject data)
    {
        foreach (var format in new[] { "UniformResourceLocatorW", "UniformResourceLocator" })
        {
            if (data.GetDataPresent(format) && data.GetData(format) is MemoryStream stream)
            {
                var text = (format.EndsWith('W') ? Encoding.Unicode : Encoding.ASCII).GetString(stream.ToArray()).TrimEnd('\0').Trim();
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    return uri.AbsoluteUri;
                }
            }
        }

        // Plain text that is just one web address.
        if ((data.GetData(DataFormats.UnicodeText) as string)?.Trim() is { Length: > 8 and < 2048 } plain && !plain.Contains(' ', StringComparison.Ordinal)
            && Uri.TryCreate(plain, UriKind.Absolute, out var single) && single.Scheme is "http" or "https")
        {
            return single.AbsoluteUri;
        }

        return null;
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase) : "Link";

    private static string FirstWords(string text)
    {
        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length == 0 ? "Text" : line.Length > 40 ? line[..39].TrimEnd() + "…" : line;
    }

    private static string Safe(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) || c == '…' ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Item" : clean.Length > 60 ? clean[..60] : clean;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Each made file gets a folder of its own, so it keeps a readable name when dragged out.</summary>
    private string Save(string id, string fileName, byte[] content)
    {
        var dir = System.IO.Path.Combine(_folder, id);
        Directory.CreateDirectory(dir);
        var path = System.IO.Path.Combine(dir, fileName);
        File.WriteAllBytes(path, content);
        return path;
    }

    private void Delete(ShelfItem item)
    {
        if (!item.Owned)
        {
            return;
        }

        try
        {
            var dir = System.IO.Path.GetDirectoryName(item.Path);
            if (dir is not null && string.Equals(System.IO.Path.GetDirectoryName(dir), _folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left behind; harmless
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_index))
            {
                _items = (JsonSerializer.Deserialize<List<ShelfItem>>(File.ReadAllText(_index), Json) ?? [])
                    .Where(i => i.Exists)
                    .Take(MaxItems)
                    .ToList();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _items = [];
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            var temp = _index + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, Json));
            File.Move(temp, _index, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the shelf still works for this run
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
