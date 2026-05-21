using System.Collections.Generic;
using FantiaMosaic.Models;
using SkiaSharp;

namespace FantiaMosaic.Services;

public interface IImageIo
{
    SKBitmap Load(string path);
    void Save(SKBitmap bitmap, string path, int quality = 95);
    SKBitmap CreateThumbnail(SKBitmap source, int longSide);
}
