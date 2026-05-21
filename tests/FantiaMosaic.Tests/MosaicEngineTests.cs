using System.Collections.Generic;
using FantiaMosaic.Models;
using FantiaMosaic.Services;
using SkiaSharp;
using Xunit;

namespace FantiaMosaic.Tests;

public class MosaicEngineTests
{
    private static SKBitmap MakeGradient(int w, int h)
    {
        var bmp = new SKBitmap(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var v = (byte)((x * 255) / (w - 1));
            bmp.SetPixel(x, y, new SKColor(v, (byte)(255 - v), (byte)((y * 255) / (h - 1))));
        }
        return bmp;
    }

    [Fact]
    public void Apply_WithNoRegions_ReturnsIdenticalCopy()
    {
        var engine = new MosaicEngine();
        using var src = MakeGradient(64, 64);
        using var dst = engine.Apply(src, new List<MosaicRegion>(), MosaicSettings.DefaultPreset());

        Assert.NotSame(src, dst);
        Assert.Equal(src.Width, dst.Width);
        Assert.Equal(src.Height, dst.Height);
        // 元と一致
        Assert.Equal(src.GetPixel(10, 10), dst.GetPixel(10, 10));
    }

    [Fact]
    public void Apply_RectangularRegion_ChangesOnlyInsidePixels()
    {
        var engine = new MosaicEngine();
        using var src = MakeGradient(128, 128);
        var regions = new[] { MosaicRegion.FromRect(new SKRect(40, 40, 90, 90)) };
        var settings = new MosaicSettings { Mode = MosaicMode.SolidFill, SolidColor = SKColors.Magenta };

        using var dst = engine.Apply(src, regions, settings);

        // 内側はマゼンタ
        Assert.Equal(SKColors.Magenta, dst.GetPixel(50, 50));
        // 外側は変化なし
        Assert.Equal(src.GetPixel(10, 10), dst.GetPixel(10, 10));
        Assert.Equal(src.GetPixel(120, 120), dst.GetPixel(120, 120));
    }

    [Fact]
    public void Apply_PixelateBlock_ProducesUniformColorWithinSingleTile()
    {
        var engine = new MosaicEngine();
        using var src = MakeGradient(400, 400);
        var settings = new MosaicSettings
        {
            Mode = MosaicMode.Pixelate,
            RelativeStrength = 0.1, // 短辺 400 × 10% = 40px タイル
            MinimumStrengthPx = 16,
        };
        var regions = new[] { MosaicRegion.FromRect(new SKRect(0, 0, 400, 400)) };

        using var dst = engine.Apply(src, regions, settings);

        // 同一タイル内の隣接ピクセルは同色になっているはず
        var a = dst.GetPixel(50, 50);
        var b = dst.GetPixel(60, 60);
        Assert.Equal(a, b);
    }

    [Fact]
    public void CheckCompliance_FlagsWeakSettings()
    {
        var engine = new MosaicEngine();
        // 短辺 1000 に対しタイル 4px しかない → ガイド違反
        var weak = new MosaicSettings { RelativeStrength = 0.001, MinimumStrengthPx = 4 };
        var r = engine.CheckCompliance(1000, 1500, weak);
        Assert.False(r.IsCompliant);
    }

    [Fact]
    public void CheckCompliance_PassesDefaultPreset()
    {
        var engine = new MosaicEngine();
        var r = engine.CheckCompliance(1920, 1080, MosaicSettings.DefaultPreset());
        Assert.True(r.IsCompliant);
    }

    [Fact]
    public void CheckCompliance_PassesEvenForSmallImage_DueToMinimumPx()
    {
        var engine = new MosaicEngine();
        // 320x240 でも下限16px が効くので OK になる想定
        var r = engine.CheckCompliance(320, 240, MosaicSettings.DefaultPreset());
        Assert.True(r.IsCompliant);
    }
}
