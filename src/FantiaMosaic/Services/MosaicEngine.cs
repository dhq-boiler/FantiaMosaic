using System;
using System.Collections.Generic;
using FantiaMosaic.Models;
using SkiaSharp;

namespace FantiaMosaic.Services;

/// <summary>
/// Fantia 2026-05-25 改定ガイドラインに準拠したモザイク処理を行うエンジン。
///
/// 設計上の要点:
///   - タイルサイズは短辺の相対値で決定し、極小画像でも MinimumStrengthPx を下回らない。
///   - PixelateThenBlur は階段状エッジを縮小時に手掛かりにさせないため、
///     タイル化後に短いガウシアンを重ねる。これがガイドの「縮小時の原型視認」を防ぐ要。
///   - 各領域は形状（矩形/楕円/ポリゴン）のクリッピングで対象外画素を保護する。
/// </summary>
public sealed class MosaicEngine : IMosaicEngine
{
    public SKBitmap Apply(SKBitmap source, IReadOnlyList<MosaicRegion> regions, MosaicSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(settings);

        var result = source.Copy();
        if (result == null)
            throw new InvalidOperationException("画像のコピーに失敗したっす");

        if (regions.Count == 0)
            return result;

        using var canvas = new SKCanvas(result);

        foreach (var region in regions)
        {
            var effective = region.Override ?? settings;
            ApplyRegion(result, canvas, region, effective);
        }

        return result;
    }

    public GuidelineCheckResult CheckCompliance(int imageWidth, int imageHeight, MosaicSettings settings)
    {
        var shortSide = Math.Min(imageWidth, imageHeight);
        var strength = settings.ResolveStrengthPx(imageWidth, imageHeight);
        var ratio = (double)strength / shortSide;

        // ガイドの「最小4pxでは縮小時に視認できる」を踏まえ、
        // 短辺の 2% 以上かつ 16px 以上を最低ラインとする。
        var compliant = ratio >= 0.02 && strength >= 16;

        var detail = compliant
            ? $"OK: 短辺 {shortSide}px に対しタイル {strength}px ({ratio:P1})。"
            : $"NG: 短辺 {shortSide}px に対しタイル {strength}px ({ratio:P1})。" +
              "RelativeStrength を 0.02 以上、MinimumStrengthPx を 16 以上にするっす。";

        return new GuidelineCheckResult(compliant, strength, shortSide, detail);
    }

    private void ApplyRegion(SKBitmap target, SKCanvas canvas, MosaicRegion region, MosaicSettings settings)
    {
        var bounds = ClampToBitmap(region.Bounds, target.Width, target.Height);
        if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1)
            return;

        canvas.Save();
        ApplyClip(canvas, region, bounds);

        switch (settings.Mode)
        {
            case MosaicMode.Pixelate:
                DrawPixelate(target, canvas, bounds, settings);
                break;

            case MosaicMode.GaussianBlur:
                DrawGaussian(target, canvas, bounds, settings);
                break;

            case MosaicMode.SolidFill:
                DrawSolid(canvas, bounds, settings, region.FillColor);
                break;

            case MosaicMode.PixelateThenBlur:
                DrawPixelate(target, canvas, bounds, settings);
                DrawPostBlur(target, canvas, bounds, settings);
                break;
        }

        canvas.Restore();
    }

    private static void ApplyClip(SKCanvas canvas, MosaicRegion region, SKRect bounds)
    {
        switch (region.Shape)
        {
            case RegionShape.Rectangle:
                canvas.ClipRect(bounds);
                break;

            case RegionShape.Ellipse:
                using (var path = new SKPath())
                {
                    path.AddOval(bounds);
                    canvas.ClipPath(path, antialias: true);
                }
                break;

            case RegionShape.Polygon:
            case RegionShape.OrientedRectangle:
                using (var path = new SKPath())
                {
                    if (region.Points.Count >= 3)
                    {
                        path.MoveTo(region.Points[0]);
                        for (var i = 1; i < region.Points.Count; i++)
                            path.LineTo(region.Points[i]);
                        path.Close();
                    }
                    canvas.ClipPath(path, antialias: true);
                }
                break;
        }
    }

    private static SKRect ClampToBitmap(SKRect r, int w, int h)
    {
        var left = Math.Max(0, MathF.Floor(r.Left));
        var top = Math.Max(0, MathF.Floor(r.Top));
        var right = Math.Min(w, MathF.Ceiling(r.Right));
        var bottom = Math.Min(h, MathF.Ceiling(r.Bottom));
        if (right <= left || bottom <= top)
            return SKRect.Empty;
        return new SKRect(left, top, right, bottom);
    }

    private static void DrawPixelate(SKBitmap target, SKCanvas canvas, SKRect bounds, MosaicSettings settings)
    {
        var tile = settings.ResolveStrengthPx(target.Width, target.Height);

        // 縮小→拡大の Nearest フィルタで純粋なタイルモザイクを生成。
        // SkiaSharp の SamplingOptions(None) で最近傍補間。
        var srcRect = SKRectI.Round(bounds);
        srcRect = SKRectI.Intersect(srcRect, new SKRectI(0, 0, target.Width, target.Height));
        if (srcRect.Width <= 0 || srcRect.Height <= 0) return;

        var downW = Math.Max(1, srcRect.Width / tile);
        var downH = Math.Max(1, srcRect.Height / tile);

        using var sub = new SKBitmap(srcRect.Width, srcRect.Height);
        using (var subCanvas = new SKCanvas(sub))
        {
            subCanvas.DrawBitmap(target,
                source: new SKRect(srcRect.Left, srcRect.Top, srcRect.Right, srcRect.Bottom),
                dest: new SKRect(0, 0, srcRect.Width, srcRect.Height));
        }

        using var down = new SKBitmap(downW, downH);
        using (var downCanvas = new SKCanvas(down))
        {
            // 平均値近似の縮小（バイリニア）
            downCanvas.DrawBitmap(sub,
                source: new SKRect(0, 0, sub.Width, sub.Height),
                dest: new SKRect(0, 0, downW, downH),
                paint: null);
        }

        // 拡大は最近傍でブロック化
        using var paint = new SKPaint
        {
            IsAntialias = false,
            FilterQuality = SKFilterQuality.None,
        };
        canvas.DrawBitmap(down,
            source: new SKRect(0, 0, downW, downH),
            dest: new SKRect(srcRect.Left, srcRect.Top, srcRect.Right, srcRect.Bottom),
            paint: paint);
    }

    private static void DrawGaussian(SKBitmap target, SKCanvas canvas, SKRect bounds, MosaicSettings settings)
    {
        var sigma = settings.ResolveStrengthPx(target.Width, target.Height) * 0.7f;
        using var paint = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(sigma, sigma),
        };

        // 対象領域だけブラーするため、ブラー入力にも領域外を含めると端が暗くなる。
        // そのため少し外周を含めて元画像を一旦描画してから、クリップで領域に絞る。
        var srcRect = SKRectI.Round(bounds);
        canvas.DrawBitmap(target,
            source: new SKRect(srcRect.Left, srcRect.Top, srcRect.Right, srcRect.Bottom),
            dest: new SKRect(srcRect.Left, srcRect.Top, srcRect.Right, srcRect.Bottom),
            paint: paint);
    }

    private static void DrawSolid(SKCanvas canvas, SKRect bounds, MosaicSettings settings, SKColor? regionColor = null)
    {
        using var paint = new SKPaint
        {
            Color = regionColor ?? settings.SolidColor,
            Style = SKPaintStyle.Fill,
            IsAntialias = false,
        };
        canvas.DrawRect(bounds, paint);
    }

    private static void DrawPostBlur(SKBitmap target, SKCanvas canvas, SKRect bounds, MosaicSettings settings)
    {
        var tile = settings.ResolveStrengthPx(target.Width, target.Height);
        var sigma = (float)(tile * settings.PostBlurRatio);
        if (sigma <= 0) return;

        using var paint = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(sigma, sigma),
        };
        var srcRect = SKRectI.Round(bounds);
        canvas.DrawBitmap(target,
            source: new SKRect(srcRect.Left, srcRect.Top, srcRect.Right, srcRect.Bottom),
            dest: new SKRect(srcRect.Left, srcRect.Top, srcRect.Right, srcRect.Bottom),
            paint: paint);
    }
}
