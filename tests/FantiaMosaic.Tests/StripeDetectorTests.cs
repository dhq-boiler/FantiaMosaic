using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FantiaMosaic.Models;
using FantiaMosaic.Services;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace FantiaMosaic.Tests;

public sealed class StripeDetectorTests
{
    private static readonly string TestsRoot = FindTestsRoot();
    private static readonly string ArtifactsDir = Path.Combine(TestsRoot, "_artifacts");
    private readonly ITestOutputHelper _output;

    public StripeDetectorTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(ArtifactsDir);
    }

    private static string FindTestsRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "Original")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException();
    }

    private static SKBitmap Load(string relative)
    {
        var path = Path.Combine(TestsRoot, relative);
        using var fs = File.OpenRead(path);
        return SKBitmap.Decode(fs) ?? throw new InvalidOperationException(path);
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
        var colors = new[]
        {
            new SKColor(0xFF, 0x40, 0x40),
            new SKColor(0x40, 0xC0, 0x40),
            new SKColor(0x40, 0x40, 0xFF),
            new SKColor(0xC0, 0x80, 0x40),
        };
        for (var i = 0; i < rects.Count; i++)
        {
            using var stroke = new SKPaint
            {
                Color = colors[i % colors.Length],
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3,
                IsAntialias = true,
            };
            var v = rects[i].Vertices();
            using var path = new SKPath();
            path.MoveTo(v[0]);
            for (var j = 1; j < v.Count; j++) path.LineTo(v[j]);
            path.Close();
            canvas.DrawPath(path, stroke);
        }
        SaveArtifact(baseImage, outName);
    }

    [Fact]
    public void StripeDetector_FindsBothOccludedRegions()
    {
        using var img = Load("Original/タイトルなし.png");
        var det = new StripeOcclusionDetector();
        var rects = det.Detect(img);
        var diag = det.LastDiagnostics;

        _output.WriteLine($"Image size: {img.Width}x{img.Height}");
        _output.WriteLine($"=== Diagnostics ===");
        _output.WriteLine($"Total components: {diag.TotalComponents}");
        _output.WriteLine($"Size rejected:    {diag.SizeRejected}");
        _output.WriteLine($"Aspect rejected:  {diag.AspectRejected}");
        _output.WriteLine($"Stripes accepted: {diag.StripesAccepted}");
        _output.WriteLine($"Groups (raw):     {diag.GroupsCount}");
        _output.WriteLine($"Groups accepted:  {diag.GroupsAccepted}");
        _output.WriteLine($"=== Stripe details ===");
        foreach (var (px, len, wid, asp, deg) in diag.StripeDetails.Take(20))
            _output.WriteLine($"  px={px} len={len:F0} wid={wid:F1} aspect={asp:F1} angle={deg:F1}°");

        _output.WriteLine($"Detected {rects.Count} stripe groups:");
        foreach (var r in rects)
            _output.WriteLine($"  center=({r.Center.X:F0},{r.Center.Y:F0}) " +
                              $"size={r.Width:F0}x{r.Height:F0} angle={r.AngleDegrees:F1}°");

        Assert.True(rects.Count >= 2, $"少なくとも 2 個の隠蔽領域を検出するはずっす ({rects.Count} 個しかない)");

        // 視覚化
        using var copy = img.Copy()!;
        DrawDetection(copy, rects, "stripe_overlay.png");

        // 線画スタイル画像なので SolidFill (ベタ塗り) で確実に隠蔽する
        var engine = new MosaicEngine();
        var settings = new MosaicSettings
        {
            Mode = MosaicMode.SolidFill,
            SolidColor = SkiaSharp.SKColors.Black,
        };
        var regions = rects.Select(r => MosaicRegion.FromPolygon(r.Vertices().ToList())).ToArray();
        using var processed = engine.Apply(img, regions, settings);
        SaveArtifact(processed, "stripe_remosaicked.png");
    }
}
