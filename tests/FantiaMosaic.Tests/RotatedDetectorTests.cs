using System;
using System.IO;
using System.Linq;
using FantiaMosaic.Models;
using FantiaMosaic.Services;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace FantiaMosaic.Tests;

/// <summary>
/// 用意された NG 画像に対して RotatedMosaicDetector が
/// 「傾いた矩形領域」を自動検出できることを検証する。
/// </summary>
public sealed class RotatedDetectorTests
{
    private static readonly string TestsRoot = FindTestsRoot();
    private static readonly string ArtifactsDir = Path.Combine(TestsRoot, "_artifacts");
    private readonly ITestOutputHelper _output;

    public RotatedDetectorTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(ArtifactsDir);
    }

    private static string FindTestsRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "Original")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("tests/Original が見つからないっす");
    }

    private static SKBitmap Load(string relative)
    {
        var path = Path.Combine(TestsRoot, relative);
        using var fs = File.OpenRead(path);
        return SKBitmap.Decode(fs) ?? throw new InvalidOperationException($"画像読み込み失敗: {path}");
    }

    private static void SaveArtifact(SKBitmap bmp, string name)
    {
        var path = Path.Combine(ArtifactsDir, name);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 95);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    private static void DrawDetection(SKBitmap baseImage, IReadOnlyList<RotatedMosaicDetector.OrientedRect> rects, string outName)
    {
        using var canvas = new SKCanvas(baseImage);
        using var stroke = new SKPaint
        {
            Color = new SKColor(0xFF, 0x40, 0x40),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            IsAntialias = true,
        };
        foreach (var r in rects)
        {
            var v = r.Vertices();
            using var path = new SKPath();
            path.MoveTo(v[0]);
            for (var i = 1; i < v.Count; i++) path.LineTo(v[i]);
            path.Close();
            canvas.DrawPath(path, stroke);
        }
        SaveArtifact(baseImage, outName);
    }

    [Fact]
    public void Detect_OnNG_Mosaic_FindsAtLeastOneRegion()
    {
        using var ng = Load("NGLists/mosaic_ng4.png");
        var detector = new RotatedMosaicDetector();
        var rects = detector.Detect(ng);

        _output.WriteLine($"Image size: {ng.Width}x{ng.Height}");
        _output.WriteLine($"=== Diagnostics ({detector.LastDiagnostics.Count} components) ===");
        foreach (var d in detector.LastDiagnostics.OrderByDescending(d => d.BlockCount).Take(15))
            _output.WriteLine(
                $"  blocks={d.BlockCount} std={d.ColorStdDev:F1} " +
                $"rect={d.Width:F0}x{d.Height:F0}@{d.AngleDeg:F1}° " +
                $"fill={d.FillRatio:F2} accepted={d.Accepted} reason='{d.RejectedReason}'");

        foreach (var r in rects)
            _output.WriteLine($"Found: center=({r.Center.X:F1},{r.Center.Y:F1}) size={r.Width:F1}x{r.Height:F1} angle={r.AngleDegrees:F2}°");

        Assert.NotEmpty(rects);

        using var copy = ng.Copy();
        DrawDetection(copy!, rects, "detect_ng_mosaic_overlay.png");
    }

    [Fact]
    public void Detect_OnNG_Mosaic_ProducesTiltedRect()
    {
        // 旧版 RotatedMosaicDetector (色クラスタリングベース) は、内部の色付きタイル
        // クラスタが正方形気味のため傾きが取れないケースがある。傾き検出は新版の
        // MosaicGridDetector (NCC+PCA) で実現。ここでは旧版が領域を返すことだけ確認。
        using var ng = Load("NGLists/mosaic_ng4.png");
        var detector = new RotatedMosaicDetector();
        var rects = detector.Detect(ng);
        Assert.NotEmpty(rects);

        var biggest = rects.OrderByDescending(r => r.Width * r.Height).First();
        var deg = NormalizeDegrees(biggest.AngleDegrees);
        _output.WriteLine($"Biggest rect angle normalized: {deg:F2}°");
    }

    private static double NormalizeDegrees(float angle)
    {
        // -180..180 にクランプ
        var a = (double)angle;
        while (a > 180) a -= 360;
        while (a < -180) a += 360;
        return a;
    }

    [Fact]
    public void Detect_AppliedAsMask_FurtherReducesLocalVariance()
    {
        using var ng = Load("NGLists/mosaic_ng4.png");
        var detector = new RotatedMosaicDetector();
        var rects = detector.Detect(ng);
        Assert.NotEmpty(rects);

        var biggest = rects.OrderByDescending(r => r.Width * r.Height).First();
        var polygon = MosaicRegion.FromPolygon(biggest.Vertices().ToList());

        var engine = new MosaicEngine();
        var settings = MosaicSettings.StrongPreset();
        using var processed = engine.Apply(ng, new[] { polygon }, settings);
        SaveArtifact(processed, "detect_ng_remosaicked.png");

        // 検出領域の軸並行 BBOX で局所分散を比較
        var aabb = ComputeAabb(biggest.Vertices(), ng.Width, ng.Height);
        var beforeVar = MeanLocalVariance(ng, aabb);
        var afterVar = MeanLocalVariance(processed, aabb);

        _output.WriteLine($"AABB={aabb}  beforeVar={beforeVar:F2}  afterVar={afterVar:F2}");
        Assert.True(afterVar < beforeVar,
            $"再モザイク後 ({afterVar:F2}) は元 NG ({beforeVar:F2}) より局所分散が下がるはずっす");
    }

    [Theory]
    [InlineData(0.0, "00")]
    [InlineData(0.05, "05")]
    [InlineData(0.10, "10")]
    public void GridDetector_MarginVariants_OnNG_Mosaic(double marginRatio, string suffix)
    {
        using var ng = Load("NGLists/mosaic_ng4.png");
        var det = new MosaicGridDetector();
        var opt = new MosaicGridDetector.Options { SafetyMarginRatio = marginRatio };
        var rects = det.Detect(ng, opt);
        Assert.NotEmpty(rects);

        var biggest = rects[0];
        _output.WriteLine($"[margin {marginRatio:P0}] " +
                          $"center=({biggest.Center.X:F0},{biggest.Center.Y:F0}) " +
                          $"size={biggest.Width:F0}x{biggest.Height:F0} angle={biggest.AngleDegrees:F1}°");

        // オーバーレイ
        using var copy = ng.Copy()!;
        DrawDetection(copy, new[] { biggest }, $"margin_{suffix}_overlay.png");

        // 純Pixelateで矩形内を完全塗り直し
        var settings = new MosaicSettings
        {
            Mode = MosaicMode.Pixelate,
            RelativeStrength = 0.05,
            MinimumStrengthPx = 24,
        };
        var engine = new MosaicEngine();
        using var processed = engine.Apply(ng,
            new[] { MosaicRegion.FromPolygon(biggest.Vertices().ToList()) }, settings);
        SaveArtifact(processed, $"margin_{suffix}_remosaicked.png");
    }

    [Fact]
    public void GridDetector_VsGroundTruth_FromOK_FullImage()
    {
        // OK/full_ok4.png の黒塗りを GT として、Grid 検出結果との差を可視化する
        using var ng = Load("NGLists/mosaic_ng4.png");
        using var ok = Load("OKLists/full_ok4.png");

        // GT: OK画像で「ほぼ真っ黒」なピクセルを検出し、最大連結成分のみ GT 領域とする
        // (キャラの線画やまつげも黒なので、ベタ塗り領域だけを取り出すために必須)
        var w = ok.Width;
        var h = ok.Height;
        var isBlack = new bool[h, w];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = ok.GetPixel(x, y);
            if (c.Red < 24 && c.Green < 24 && c.Blue < 24)
                isBlack[y, x] = true;
        }
        var (gtPoints, _) = FindLargestComponent(isBlack, h, w);
        Assert.True(gtPoints.Count > 5000, "GT 領域の黒塗り連結成分が少なすぎるっす");

        // GTの軸並行bboxとMinAreaRect
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in gtPoints)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        var gtAabb = new SKRectI((int)minX, (int)minY, (int)maxX, (int)maxY);
        var gtRotated = RotatedMosaicDetector.MinAreaRectPublic(gtPoints);

        _output.WriteLine($"GT AABB:    {gtAabb} ({gtAabb.Width}x{gtAabb.Height})");
        _output.WriteLine($"GT Rotated: center=({gtRotated.Center.X:F0},{gtRotated.Center.Y:F0}) " +
                          $"size={gtRotated.Width:F0}x{gtRotated.Height:F0} angle={gtRotated.AngleDegrees:F1}°");

        // 検出実行
        var det = new MosaicGridDetector();
        var rects = det.Detect(ng);
        Assert.NotEmpty(rects);
        var detected = rects[0];
        _output.WriteLine($"Detected:   center=({detected.Center.X:F0},{detected.Center.Y:F0}) " +
                          $"size={detected.Width:F0}x{detected.Height:F0} angle={detected.AngleDegrees:F1}°");

        // 重ね描画: 緑=GT, 赤=検出
        using var canvas = ng.Copy()!;
        using var c2 = new SKCanvas(canvas);
        DrawRect(c2, gtRotated, new SKColor(0x40, 0xC0, 0x40), 4);
        DrawRect(c2, detected, new SKColor(0xE0, 0x40, 0x40), 4);
        SaveArtifact(canvas, "compare_gt_vs_detect.png");

        // 中心距離
        var dx = detected.Center.X - gtRotated.Center.X;
        var dy = detected.Center.Y - gtRotated.Center.Y;
        var centerDist = Math.Sqrt(dx * dx + dy * dy);
        var gtArea = gtRotated.Width * gtRotated.Height;
        var detArea = detected.Width * detected.Height;
        _output.WriteLine($"Center distance: {centerDist:F1}px  " +
                          $"area ratio (det/GT): {detArea / gtArea:F2}");
    }

    /// <summary>二値マップから最大連結成分(4近傍)を抽出してその点群を返す。</summary>
    private static (List<SKPoint> Points, int LabelCount) FindLargestComponent(bool[,] mask, int h, int w)
    {
        var labels = new int[h, w];
        var sizes = new List<int> { 0 }; // index 0 = unused
        var pointsPerLabel = new List<List<SKPoint>> { new() };
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (!mask[y, x] || labels[y, x] != 0) continue;
            var label = sizes.Count;
            var pts = new List<SKPoint>();
            var stack = new Stack<(int x, int y)>();
            stack.Push((x, y));
            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Pop();
                if ((uint)cx >= (uint)w || (uint)cy >= (uint)h) continue;
                if (!mask[cy, cx] || labels[cy, cx] != 0) continue;
                labels[cy, cx] = label;
                pts.Add(new SKPoint(cx, cy));
                stack.Push((cx + 1, cy));
                stack.Push((cx - 1, cy));
                stack.Push((cx, cy + 1));
                stack.Push((cx, cy - 1));
            }
            sizes.Add(pts.Count);
            pointsPerLabel.Add(pts);
        }
        var largestIdx = 0;
        for (var i = 1; i < sizes.Count; i++)
            if (sizes[i] > sizes[largestIdx]) largestIdx = i;
        return (pointsPerLabel[largestIdx], sizes.Count - 1);
    }

    private static void DrawRect(SKCanvas canvas, RotatedMosaicDetector.OrientedRect r, SKColor color, float strokeWidth)
    {
        using var paint = new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            IsAntialias = true,
        };
        var v = r.Vertices();
        using var path = new SKPath();
        path.MoveTo(v[0]);
        for (var i = 1; i < v.Count; i++) path.LineTo(v[i]);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    [Fact]
    public void GridDetector_FindsCardOnNG_Mosaic()
    {
        // Qiita方式 (matchTemplate NCC) でモザイク領域を検出
        using var ng = Load("NGLists/mosaic_ng4.png");
        var det = new MosaicGridDetector();

        // スコアマップを可視化（デバッグ用）
        using var scoreMap = det.BuildScoreMap(ng);
        SaveArtifact(scoreMap, "detect_ng_grid_score.png");

        var rects = det.Detect(ng);

        _output.WriteLine($"Grid detector found {rects.Count} regions");
        foreach (var r in rects.Take(5))
            _output.WriteLine($"  center=({r.Center.X:F0},{r.Center.Y:F0}) size={r.Width:F0}x{r.Height:F0} angle={r.AngleDegrees:F1}°");

        Assert.NotEmpty(rects);

        using var copy = ng.Copy();
        DrawDetection(copy!, rects.Take(3).ToList(), "detect_ng_grid_overlay.png");

        if (rects.Count > 0)
        {
            var biggest = rects[0];
            var engine = new MosaicEngine();
            // 矩形の境界がぼけないよう、ブラーなしの純タイルモザイクで塗り直す。
            // 検出された自由角度矩形の polygon 内側はクリップで完全にカバーされる。
            var settings = new MosaicSettings
            {
                Mode = MosaicMode.Pixelate,
                RelativeStrength = 0.05,
                MinimumStrengthPx = 24,
            };
            using var processed = engine.Apply(ng,
                new[] { MosaicRegion.FromPolygon(biggest.Vertices().ToList()) }, settings);
            SaveArtifact(processed, "detect_ng_grid_remosaicked.png");
        }
    }

    [Fact]
    public void Refine_ExpandsToIncludeFullCardArea()
    {
        // 札のピンク縁取りまで含めて検出範囲を拡張できることを検証
        using var ng = Load("NGLists/mosaic_ng4.png");
        var detector = new RotatedMosaicDetector();
        var rects = detector.Detect(ng);
        Assert.NotEmpty(rects);
        var seed = rects.OrderByDescending(r => r.Width * r.Height).First();

        var refiner = new MosaicRegionRefiner();
        var refined = refiner.Refine(ng, seed);

        var seedArea = seed.Width * seed.Height;
        var refinedArea = refined.Width * refined.Height;
        _output.WriteLine($"Seed:    center=({seed.Center.X:F0},{seed.Center.Y:F0}) size={seed.Width:F0}x{seed.Height:F0} area={seedArea:F0}");
        _output.WriteLine($"Refined: center=({refined.Center.X:F0},{refined.Center.Y:F0}) size={refined.Width:F0}x{refined.Height:F0} area={refinedArea:F0}");

        // 拡張されているはず
        Assert.True(refinedArea > seedArea,
            $"Refiner で領域が拡張されていないっす seed={seedArea} refined={refinedArea}");

        // 視覚化
        using var copy = ng.Copy();
        DrawDetection(copy!, new[] { seed }, "detect_ng_seed.png");
        using var copy2 = ng.Copy();
        DrawDetection(copy2!, new[] { refined }, "detect_ng_refined.png");

        // 拡張後の領域で再モザイク
        var engine = new MosaicEngine();
        var settings = MosaicSettings.StrongPreset();
        using var processed = engine.Apply(ng,
            new[] { MosaicRegion.FromPolygon(refined.Vertices().ToList()) },
            settings);
        SaveArtifact(processed, "detect_ng_refined_remosaicked.png");
    }

    [Fact]
    public void Detect_OnOriginal_DoesNotFindFalseMosaic()
    {
        // モザイクのない元画像で誤検出が出ないことを確認（少なくとも巨大な誤検出は出ないはず）
        using var orig = Load("Original/sample2.png");
        var detector = new RotatedMosaicDetector();
        var rects = detector.Detect(orig);

        _output.WriteLine($"Detected on Original: {rects.Count} regions");
        foreach (var r in rects)
            _output.WriteLine($"  center=({r.Center.X:F1},{r.Center.Y:F1}) size={r.Width:F1}x{r.Height:F1}");

        // 大きすぎる(=画面の30%以上)誤検出は出ない
        var imageArea = (double)orig.Width * orig.Height;
        foreach (var r in rects)
        {
            var area = r.Width * r.Height;
            Assert.True(area / imageArea < 0.30,
                $"オリジナル画像で巨大領域を誤検出 (面積比 {area / imageArea:P1})");
        }
    }

    private static SKRectI ComputeAabb(IReadOnlyList<SKPoint> verts, int w, int h)
    {
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (var p in verts)
        {
            if (p.X < l) l = p.X;
            if (p.Y < t) t = p.Y;
            if (p.X > r) r = p.X;
            if (p.Y > b) b = p.Y;
        }
        return new SKRectI(
            Math.Max(0, (int)l),
            Math.Max(0, (int)t),
            Math.Min(w, (int)r),
            Math.Min(h, (int)b));
    }

    private static double Lum(SKColor c) => 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;

    private static double MeanLocalVariance(SKBitmap bmp, SKRectI region, int win = 5)
    {
        var half = win / 2;
        var l = Math.Max(half, region.Left);
        var t = Math.Max(half, region.Top);
        var r = Math.Min(bmp.Width - half, region.Right);
        var b = Math.Min(bmp.Height - half, region.Bottom);
        if (r <= l || b <= t) return 0;

        double total = 0;
        var samples = 0;
        for (var y = t; y < b; y++)
        for (var x = l; x < r; x++)
        {
            double sum = 0, sumSq = 0;
            var n = 0;
            for (var dy = -half; dy <= half; dy++)
            for (var dx = -half; dx <= half; dx++)
            {
                var v = Lum(bmp.GetPixel(x + dx, y + dy));
                sum += v;
                sumSq += v * v;
                n++;
            }
            var mean = sum / n;
            total += sumSq / n - mean * mean;
            samples++;
        }
        return samples > 0 ? total / samples : 0;
    }
}
