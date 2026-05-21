using System;
using System.IO;
using FantiaMosaic.Models;
using FantiaMosaic.Services;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace FantiaMosaic.Tests;

/// <summary>
/// 用意されたテスト画像 (tests/Original, tests/NGLists, tests/OKLists) を使った統合テスト。
///
/// 検証の考え方:
///   - 当ツールでOriginalにモザイクを適用した結果が、
///     NG例より明確に「局所分散（=エッジ密度）が低い」状態になることを確認する。
///   - 同時に、生成画像を tests/_artifacts/ に保存して目視確認できるようにする。
///   - 適合チェック (CheckCompliance) も通ることを確認する。
/// </summary>
public sealed class IntegrationTests
{
    private static readonly string TestsRoot = FindTestsRoot();
    private static readonly string ArtifactsDir = Path.Combine(TestsRoot, "_artifacts");
    private readonly ITestOutputHelper _output;

    public IntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(ArtifactsDir);
    }

    // ===== ヘルパ =====

    private static string FindTestsRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "Original")))
            dir = Directory.GetParent(dir)?.FullName;
        if (dir == null) throw new InvalidOperationException("tests/Original が見つからないっす");
        return dir;
    }

    private static SKBitmap Load(string relative)
    {
        var path = Path.Combine(TestsRoot, relative);
        using var fs = File.OpenRead(path);
        return SKBitmap.Decode(fs) ?? throw new InvalidOperationException($"画像読み込み失敗: {path}");
    }

    private static double Lum(SKColor c) => 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;

    /// <summary>
    /// Original と参照画像を比べて、変更されたピクセルの境界矩形を返す。
    /// </summary>
    private static SKRectI ComputeDiffBounds(SKBitmap a, SKBitmap b, int threshold = 30)
    {
        var w = Math.Min(a.Width, b.Width);
        var h = Math.Min(a.Height, b.Height);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var ca = a.GetPixel(x, y);
            var cb = b.GetPixel(x, y);
            var d = Math.Abs(ca.Red - cb.Red) + Math.Abs(ca.Green - cb.Green) + Math.Abs(ca.Blue - cb.Blue);
            if (d > threshold)
            {
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }
        if (minX > maxX) return SKRectI.Empty;
        return new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }

    /// <summary>
    /// 矩形内の平均的な局所分散（5x5 窓のLuma分散の平均）を返す。
    /// 値が大きいほどエッジ/細部が残っている=モザイクが甘い。
    /// </summary>
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

    private static void SaveArtifact(SKBitmap bmp, string name)
    {
        var path = Path.Combine(ArtifactsDir, name);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 95);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    // ===== テスト =====

    [Fact]
    public void Original_DiffAgainst_OK_FindsSignRegion()
    {
        using var orig = Load("Original/sample2.png");
        using var ok = Load("OKLists/mosaic_ok4.png");
        var bounds = ComputeDiffBounds(orig, ok, 30);

        _output.WriteLine($"Diff bounds vs OK/mosaic = {bounds} ({bounds.Width}x{bounds.Height})");
        Assert.True(bounds.Width > 50 && bounds.Height > 30,
            $"差分領域が小さすぎるっす: {bounds}");
    }

    [Fact]
    public void Pixelate_AppliedToOriginal_IsSmootherThan_NG_Mosaic()
    {
        using var orig = Load("Original/sample2.png");
        using var ng = Load("NGLists/mosaic_ng4.png");
        using var ok = Load("OKLists/mosaic_ok4.png");
        var region = ComputeDiffBounds(orig, ok, 30);

        var engine = new MosaicEngine();
        var settings = MosaicSettings.DefaultPreset();
        var regions = new[] { MosaicRegion.FromRect(new SKRect(region.Left, region.Top, region.Right, region.Bottom)) };
        using var processed = engine.Apply(orig, regions, settings);
        SaveArtifact(processed, "out_pixelate_default.png");

        var ngVar = MeanLocalVariance(ng, region);
        var okVar = MeanLocalVariance(ok, region);
        var procVar = MeanLocalVariance(processed, region);

        _output.WriteLine($"LocalVar: NG={ngVar:F2}  OK={okVar:F2}  Processed={procVar:F2}");

        // 当ツール出力は NG より平滑（=細部が消えている）はず
        Assert.True(procVar < ngVar,
            $"NG ({ngVar:F2}) より処理結果 ({procVar:F2}) が滑らかになってないっす");

        // 適合チェックも通っているはず
        var check = engine.CheckCompliance(orig.Width, orig.Height, settings);
        Assert.True(check.IsCompliant, $"Compliance NG: {check.Detail}");
    }

    [Fact]
    public void Strong_PixelateThenBlur_ApproachesOK_Quality()
    {
        using var orig = Load("Original/sample2.png");
        using var ok = Load("OKLists/mosaic_ok4.png");
        var region = ComputeDiffBounds(orig, ok, 30);

        var engine = new MosaicEngine();
        var settings = MosaicSettings.StrongPreset();
        var regions = new[] { MosaicRegion.FromRect(new SKRect(region.Left, region.Top, region.Right, region.Bottom)) };
        using var processed = engine.Apply(orig, regions, settings);
        SaveArtifact(processed, "out_pixelate_strong.png");

        var okVar = MeanLocalVariance(ok, region);
        var procVar = MeanLocalVariance(processed, region);
        _output.WriteLine($"Strong preset: OK={okVar:F2}  Processed={procVar:F2}");

        // 強プリセットならOK相当以下になっているはず（±50%のマージン）
        Assert.True(procVar <= okVar * 1.5,
            $"強プリセットでもOK ({okVar:F2}) に対し処理 ({procVar:F2}) が粗すぎるっす");
    }

    [Fact]
    public void GaussianBlur_AppliedToOriginal_IsSmootherThan_NG_Bokashi()
    {
        using var orig = Load("Original/sample2.png");
        using var ng = Load("NGLists/bokashi_ng4.png");
        using var ok = Load("OKLists/bokashi_ok4.png");
        var region = ComputeDiffBounds(orig, ok, 30);

        var engine = new MosaicEngine();
        var settings = new MosaicSettings
        {
            Mode = MosaicMode.GaussianBlur,
            RelativeStrength = 0.05,
            MinimumStrengthPx = 24,
        };
        var regions = new[] { MosaicRegion.FromRect(new SKRect(region.Left, region.Top, region.Right, region.Bottom)) };
        using var processed = engine.Apply(orig, regions, settings);
        SaveArtifact(processed, "out_gaussian.png");

        var ngVar = MeanLocalVariance(ng, region);
        var procVar = MeanLocalVariance(processed, region);
        _output.WriteLine($"Blur: NG={ngVar:F2}  Processed={procVar:F2}");

        Assert.True(procVar < ngVar,
            $"NG ({ngVar:F2}) よりブラー出力 ({procVar:F2}) が滑らかになってないっす");
    }

    [Fact]
    public void SolidFill_AppliedToOriginal_HasNearZeroVariance()
    {
        using var orig = Load("Original/sample2.png");
        using var ok = Load("OKLists/full_ok4.png");
        var region = ComputeDiffBounds(orig, ok, 30);

        var engine = new MosaicEngine();
        var settings = new MosaicSettings
        {
            Mode = MosaicMode.SolidFill,
            SolidColor = SKColors.Black,
        };
        var regions = new[] { MosaicRegion.FromRect(new SKRect(region.Left, region.Top, region.Right, region.Bottom)) };
        using var processed = engine.Apply(orig, regions, settings);
        SaveArtifact(processed, "out_solid.png");

        // 境界に微妙な縁が残る可能性はあるが、領域内側中央は完全ベタ
        var inner = new SKRectI(region.Left + 5, region.Top + 5, region.Right - 5, region.Bottom - 5);
        var v = MeanLocalVariance(processed, inner);
        _output.WriteLine($"Solid inner variance = {v:F4}");
        Assert.True(v < 1.0, $"ベタ塗り内側にエッジが残ってる: {v:F4}");
    }

    [Fact]
    public void NG_References_ShowHigher_LocalVariance_Than_OK_References()
    {
        // 各モードでNG画像はOK画像より細部が残っていることを確認（テスト画像自体の妥当性チェック）
        using var orig = Load("Original/sample2.png");

        Verify("mosaic", "NGLists/mosaic_ng4.png", "OKLists/mosaic_ok4.png");
        Verify("bokashi", "NGLists/bokashi_ng4.png", "OKLists/bokashi_ok4.png");
        // 「full」は領域が違うかもしれないのでスキップ可

        void Verify(string label, string ngRel, string okRel)
        {
            using var ng = Load(ngRel);
            using var ok = Load(okRel);
            var region = ComputeDiffBounds(orig, ok, 30);
            var ngVar = MeanLocalVariance(ng, region);
            var okVar = MeanLocalVariance(ok, region);
            _output.WriteLine($"[{label}] NG={ngVar:F2}  OK={okVar:F2}");
            Assert.True(ngVar > okVar,
                $"[{label}] NGの方がOKより細部が残ってるはずっす (NG={ngVar:F2}, OK={okVar:F2})");
        }
    }

    [Fact]
    public void BatchExport_OnOriginal_ProducesOutputFile()
    {
        using var orig = Load("Original/sample2.png");
        using var ok = Load("OKLists/mosaic_ok4.png");
        var region = ComputeDiffBounds(orig, ok, 30);

        // 単一ジョブのバッチエクスポート
        var job = new ImageJob
        {
            SourcePath = Path.Combine(TestsRoot, "Original", "sample2.png"),
            OriginalWidth = orig.Width,
            OriginalHeight = orig.Height,
        };
        job.Regions.Add(MosaicRegion.FromRect(new SKRect(region.Left, region.Top, region.Right, region.Bottom)));

        var outDir = Path.Combine(ArtifactsDir, "batch_out");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        Directory.CreateDirectory(outDir);

        var exporter = new BatchExporter(new MosaicEngine(), new SkiaImageIo());
        var settings = MosaicSettings.DefaultPreset();
        settings.ThumbnailLongSide = 320;

        var result = exporter.ExportAsync(new[] { job }, settings, outDir).GetAwaiter().GetResult();

        _output.WriteLine($"Batch: success={result.Success} failed={result.Failed} elapsed={result.Elapsed}");
        Assert.Equal(1, result.Success);
        Assert.Equal(0, result.Failed);

        var mainOut = Path.Combine(outDir, "sample2_mosaic.png");
        var thumbOut = Path.Combine(outDir, "sample2_mosaic_thumb.png");
        Assert.True(File.Exists(mainOut), "本体出力が無いっす");
        Assert.True(File.Exists(thumbOut), "サムネ出力が無いっす");
    }
}
