using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CsCode.Core.Platform;
using CsCode.Editor.Threading;

namespace CsCode.Editor.Markdown.Preview;

/// <summary>
/// Images of one preview: local files (relative to the Markdown file, through <see cref="IFileSystem"/>, so a remote ssh: workspace works too),
/// <c>data:</c> URIs, and http(s) URLs only when <see cref="MarkdownPreviewSettings.AllowRemoteImages"/> is on. Reading happens on the file
/// system's lane, decoding on the thread pool; the UI only receives the finished bitmap. Decoded images are cached per preview (bounded) and
/// downscaled to <see cref="MaxDecodeWidth"/> pixels so a photo in a README costs a few MB, not 50. A failed, blocked or unsupported image
/// (SVG: there is no SVG renderer) shows its alt text instead.
/// </summary>
internal sealed class MarkdownImageLoader(IFileSystem files, IUiThread ui, Func<Uri?> baseUri, Func<bool> allowRemote, Func<MarkdownPreviewStyle> style) : IDisposable
{
    public const int MaxDecodeWidth = 2048;
    public const int MaxBytes = 20 * 1024 * 1024;
    private const int MaxCached = 64;

    private static readonly Lazy<HttpClient> Http = new(() => new HttpClient { Timeout = TimeSpan.FromSeconds(15) });

    private readonly Dictionary<string, Task<Bitmap?>> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    // The bitmaps of the cached entries (disposed with the preview). An entry dropped from the cache leaves this set too: an image control may
    // still show its bitmap, so it is not disposed, and once no control does the GC frees it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Bitmap, byte> _decoded = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>Raised on the UI thread when an image finished loading (its size changes the layout).</summary>
    public event Action? Loaded;

    /// <summary>Images that finished loading as a bitmap so far (tests).</summary>
    public int LoadedCount { get; private set; }

    /// <summary>The control for one image: its alt text until the bitmap arrives (or for good, when it cannot be shown).</summary>
    public Control Create(string url, string alt)
    {
        var s = style();
        var host = new Border { MinHeight = 1 };
        var placeholder = new TextBlock
        {
            Text = alt.Length > 0 ? alt : "image", FontFamily = s.Body, FontSize = s.FontSize * 0.9, Foreground = s.Muted, FontStyle = FontStyle.Italic,
        };
        host.Child = placeholder;
        var resolved = Resolve(url);
        if (resolved.Error is { } why)
        {
            ToolTip.SetTip(host, why);
            return host;
        }
        ToolTip.SetTip(host, alt.Length > 0 ? alt : url);
        _ = ShowAsync(host, resolved.Key!, resolved.Load!, url);
        return host;
    }

    private async Task ShowAsync(Border host, string key, Func<CancellationToken, Task<byte[]?>> load, string url)
    {
        Bitmap? bitmap;
        try { bitmap = await Get(key, load).ConfigureAwait(true); }
        catch (Exception) { bitmap = null; }
        if (_disposed) return;
        void Apply()
        {
            if (_disposed) return;
            if (bitmap is null)
            {
                ToolTip.SetTip(host, "Could not load the image: " + url);
                return;
            }
            host.Child = new Image
            {
                Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxWidth = bitmap.Size.Width,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            LoadedCount++;
            Loaded?.Invoke();
        }
        if (ui.CheckAccess()) Apply(); else ui.Post(Apply);
    }

    private Task<Bitmap?> Get(string key, Func<CancellationToken, Task<byte[]?>> load)
    {
        if (_cache.TryGetValue(key, out var known)) return known;
        var ct = _cts.Token;
        var task = Task.Run(async () =>
        {
            var bytes = await load(ct).ConfigureAwait(false);
            var bitmap = bytes is null ? null : Decode(bytes);
            if (bitmap is null) return null;
            _decoded.TryAdd(bitmap, 0);
            if (Volatile.Read(ref _disposed))
            {
                // decoded after the preview went away: nobody will show it
                DisposeDecoded();
                return null;
            }
            return bitmap;
        }, ct);
        _cache[key] = task;
        _order.Enqueue(key);
        while (_order.Count > MaxCached)
        {
            var evicted = _order.Dequeue();
            if (_cache.Remove(evicted, out var dropped)) _ = ForgetAsync(dropped);
        }
        return task;
    }

    private async Task ForgetAsync(Task<Bitmap?> dropped)
    {
        try
        {
            if (await dropped.ConfigureAwait(false) is { } bitmap) _decoded.TryRemove(bitmap, out _);
        }
        catch (Exception)
        {
            // failed or cancelled: it holds no bitmap
        }
    }

    /// <summary>Decodes PNG, JPEG, GIF (first frame), BMP, WebP and ICO; null for anything else (SVG included). Wider images are downscaled.</summary>
    internal static Bitmap? Decode(byte[] bytes)
    {
        if (bytes.Length == 0 || LooksLikeSvg(bytes)) return null;
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(stream);
            if (bitmap.PixelSize.Width <= MaxDecodeWidth) return bitmap;
            bitmap.Dispose();
            stream.Position = 0;
            return Bitmap.DecodeToWidth(stream, MaxDecodeWidth);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool LooksLikeSvg(byte[] bytes)
    {
        var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }

    internal readonly record struct Resolved(string? Key, Func<CancellationToken, Task<byte[]?>>? Load, string? Error);

    /// <summary>Where an image comes from, or why it is not shown.</summary>
    internal Resolved Resolve(string url)
    {
        url = url.Trim();
        if (url.Length == 0) return new(null, null, "The image has no address.");
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma < 0 || !url.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return new(null, null, "Only base64 data: images are supported.");
            var payload = url[(comma + 1)..];
            return new("data:" + payload.Length + ":" + payload.GetHashCode(), _ =>
            {
                try { return Task.FromResult<byte[]?>(Convert.FromBase64String(payload)); }
                catch (FormatException) { return Task.FromResult<byte[]?>(null); }
            }, null);
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            if (!allowRemote()) return new(null, null, $"Remote images are not loaded ({MarkdownPreviewSettings.AllowRemoteImages} is off): {url}");
            return new(absolute.AbsoluteUri, ct => FetchAsync(absolute, ct), null);
        }
        Uri target;
        if (absolute is not null && absolute.Scheme is not ("file" or "ssh" or "vscode-remote")) return new(null, null, "Unsupported image address: " + url);
        if (absolute is not null) target = absolute;
        else if (baseUri() is { } doc && Uri.TryCreate(doc, StripQuery(url).Replace(" ", "%20", StringComparison.Ordinal), out var relative)) target = relative;
        else return new(null, null, "The image's location is unknown: " + url);
        return new(target.AbsoluteUri, async ct =>
        {
            var bytes = await files.ReadFileAsync(target, ct).ConfigureAwait(false);
            return bytes.Length > MaxBytes ? null : bytes;
        }, null);
    }

    private static string StripQuery(string url)
    {
        var cut = url.IndexOfAny(['?', '#']);
        return cut >= 0 ? url[..cut] : url;
    }

    private static async Task<byte[]?> FetchAsync(Uri uri, CancellationToken ct)
    {
        using var response = await Http.Value.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes) return null;
        }
        return buffer.ToArray();
    }

    /// <summary>Bitmaps the preview holds for its cache (tests): at most the cache's size.</summary>
    internal int DecodedCount => _decoded.Count;

    private void DisposeDecoded()
    {
        foreach (var bitmap in _decoded.Keys)
            if (_decoded.TryRemove(bitmap, out _)) bitmap.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        DisposeDecoded();
        _cache.Clear();
        _order.Clear();
        Loaded = null;
        _cts.Dispose();
    }
}
