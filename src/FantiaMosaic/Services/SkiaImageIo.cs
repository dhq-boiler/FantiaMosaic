using System;
using System.IO;
using SkiaSharp;

namespace FantiaMosaic.Services;

public sealed class SkiaImageIo : IImageIo
{
    public SKBitmap Load(string path)
    {
        using var stream = File.OpenRead(path);
        var bmp = SKBitmap.Decode(stream)
            ?? throw new InvalidOperationException($"画像のデコードに失敗したっす: {path}");
        return bmp;
    }

    public void Save(SKBitmap bitmap, string path, int quality = 95)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var format = DetectFormat(path);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    public SKBitmap CreateThumbnail(SKBitmap source, int longSide)
    {
        if (longSide <= 0)
            throw new ArgumentOutOfRangeException(nameof(longSide));

        var scale = (double)longSide / Math.Max(source.Width, source.Height);
        if (scale >= 1.0)
            return source.Copy()!;

        var w = Math.Max(1, (int)Math.Round(source.Width * scale));
        var h = Math.Max(1, (int)Math.Round(source.Height * scale));
        var info = new SKImageInfo(w, h, source.ColorType, source.AlphaType);

        var resized = new SKBitmap(info);
        source.ScalePixels(resized, SKFilterQuality.High);
        return resized;
    }

    private static SKEncodedImageFormat DetectFormat(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            ".bmp" => SKEncodedImageFormat.Bmp,
            _ => SKEncodedImageFormat.Png,
        };
    }
}
