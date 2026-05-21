using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FantiaMosaic.Models;
using SkiaSharp;

namespace FantiaMosaic.Services;

/// <summary>
/// ユーザー指定の「正解 (GT) 矩形」に合うように StripeOcclusionDetector のパラメータを探索する。
///
/// アルゴリズム: 座標降下法 (coordinate descent) を 2〜3 周回す。
///   各パラメータについて、他を固定して候補値を順次試す。
///   スコアが最大のものを採用し、収束まで繰り返す。
/// 評価: 検出矩形 vs GT 矩形 の IoU 合計 - 余分な検出のペナルティ。
/// </summary>
public sealed class StripeParameterTuner
{
    private readonly StripeOcclusionDetector _detector = new();

    public sealed record TuneResult(
        StripeOcclusionDetector.Options BestOptions,
        double BestScore,
        int Iterations,
        int Evaluations,
        TimeSpan Elapsed);

    public sealed record Progress(int Done, int Total, double CurrentBestScore);

    /// <summary>1画像分のサンプル。</summary>
    public sealed record Sample(SKBitmap Image, IReadOnlyList<MosaicRegion> GroundTruth);

    /// <summary>
    /// 単一画像でのチューニング (TuneMultiAsync のラッパー)。
    /// </summary>
    public Task<TuneResult> TuneAsync(
        SKBitmap bitmap,
        IReadOnlyList<MosaicRegion> groundTruth,
        StripeOcclusionDetector.Options? seedOptions = null,
        IProgress<Progress>? progress = null,
        CancellationToken cancellationToken = default)
        => TuneMultiAsync(new[] { new Sample(bitmap, groundTruth) }, seedOptions, progress, cancellationToken);

    /// <summary>
    /// 複数画像でのチューニング。全サンプルの平均 IoU を最大化する。
    /// 各サンプルで GT を描いた画像のみを渡すこと。
    /// </summary>
    public async Task<TuneResult> TuneMultiAsync(
        IReadOnlyList<Sample> samples,
        StripeOcclusionDetector.Options? seedOptions = null,
        IProgress<Progress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (samples.Count == 0)
            throw new ArgumentException("学習サンプルが空っす。");
        if (samples.Any(s => s.GroundTruth.Count == 0))
            throw new ArgumentException("GT が空のサンプルがあるっす。全サンプルに正解矩形が必要っす。");
        var groundTruth = samples[0].GroundTruth; // 単一画像時のラッパーから来る場合
        var bitmap = samples[0].Image;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var current = Clone(seedOptions ?? new StripeOcclusionDetector.Options());

        // 全サンプルの GT を AABB 化しておく
        var sampleData = samples.Select(s =>
            (Image: s.Image, GtAabbs: s.GroundTruth.Select(r => r.Bounds).ToList())).ToList();

        var paramSpecs = BuildParameterSpace();
        var loops = 3; // 座標降下を 3 周

        double EvaluateAll(StripeOcclusionDetector.Options opt)
        {
            double sum = 0;
            foreach (var s in sampleData)
                sum += EvaluateOne(s.Image, opt, s.GtAabbs);
            return sum / sampleData.Count;
        }

        var bestScore = await Task.Run(() => EvaluateAll(current), cancellationToken);
        var evaluations = 1;
        var done = 0;
        var totalSteps = paramSpecs.Count * loops;
        progress?.Report(new Progress(done, totalSteps, bestScore));

        for (var loop = 0; loop < loops; loop++)
        {
            foreach (var spec in paramSpecs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var paramBest = bestScore;
                var paramBestOptions = current;

                foreach (var candidate in spec.Candidates)
                {
                    var trial = Clone(current);
                    spec.Set(trial, candidate);
                    var score = await Task.Run(() => EvaluateAll(trial), cancellationToken);
                    evaluations++;

                    if (score > paramBest)
                    {
                        paramBest = score;
                        paramBestOptions = trial;
                    }
                }

                if (paramBest > bestScore)
                {
                    bestScore = paramBest;
                    current = paramBestOptions;
                }
                done++;
                progress?.Report(new Progress(done, totalSteps, bestScore));
            }
        }

        sw.Stop();
        return new TuneResult(current, bestScore, loops, evaluations, sw.Elapsed);
    }

    private double EvaluateOne(SKBitmap bitmap, StripeOcclusionDetector.Options opt, List<SKRect> gtAabbs)
    {
        var detected = _detector.Detect(bitmap, opt);
        var detAabbs = detected.Select(r => ToAabb(r.Vertices())).ToList();

        // 各 GT について、最大 IoU の検出を選ぶ (Greedy matching)
        double iouSum = 0;
        foreach (var gt in gtAabbs)
        {
            double best = 0;
            foreach (var d in detAabbs)
            {
                var iou = IoU(gt, d);
                if (iou > best) best = iou;
            }
            iouSum += best;
        }
        var meanGtIoU = iouSum / gtAabbs.Count;

        // 余分な検出 (GT 数を超える分) は軽くペナルティ
        var extra = Math.Max(0, detAabbs.Count - gtAabbs.Count);
        var penalty = extra * 0.05;

        // 検出ゼロには大きなペナルティ
        if (detAabbs.Count == 0) return -1;

        return meanGtIoU - penalty;
    }

    private static SKRect ToAabb(IReadOnlyList<SKPoint> verts)
    {
        float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (var p in verts)
        {
            if (p.X < l) l = p.X;
            if (p.Y < t) t = p.Y;
            if (p.X > r) r = p.X;
            if (p.Y > b) b = p.Y;
        }
        return new SKRect(l, t, r, b);
    }

    private static double IoU(SKRect a, SKRect b)
    {
        var ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var iy = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        var inter = ix * iy;
        var ua = a.Width * a.Height;
        var ub = b.Width * b.Height;
        var union = ua + ub - inter;
        if (union <= 0) return 0;
        return inter / union;
    }

    private static StripeOcclusionDetector.Options Clone(StripeOcclusionDetector.Options src) => new()
    {
        DarkLumaThreshold = src.DarkLumaThreshold,
        MinAspectRatio = src.MinAspectRatio,
        MinStripePixels = src.MinStripePixels,
        MaxStripePixels = src.MaxStripePixels,
        MinStripeLengthPx = src.MinStripeLengthPx,
        MinStripeWidthPx = src.MinStripeWidthPx,
        MaxStripeWidthPx = src.MaxStripeWidthPx,
        MaxStripeLengthPx = src.MaxStripeLengthPx,
        GroupAngleToleranceDeg = src.GroupAngleToleranceDeg,
        GroupDistanceShortSideMul = src.GroupDistanceShortSideMul,
        MinStripesPerGroup = src.MinStripesPerGroup,
        Downsample = src.Downsample,
        SafetyMarginRatio = src.SafetyMarginRatio,
        MinShortToLongRatio = src.MinShortToLongRatio,
        DilationRadius = src.DilationRadius,
        OpeningRadius = src.OpeningRadius,
        ClosingRadius = src.ClosingRadius,
    };

    private sealed class ParamSpec
    {
        public string Name = string.Empty;
        public Action<StripeOcclusionDetector.Options, double> Set = (_, _) => { };
        public double[] Candidates = Array.Empty<double>();
    }

    /// <summary>
    /// 探索するパラメータと候補値のリストを構築。
    /// 候補値は経験的な範囲で粗く設定。
    /// </summary>
    private static List<ParamSpec> BuildParameterSpace() => new()
    {
        new ParamSpec
        {
            Name = "DarkLumaThreshold",
            Set = (o, v) => o.DarkLumaThreshold = (int)v,
            Candidates = new double[] { 50, 80, 100, 130, 160, 190 },
        },
        new ParamSpec
        {
            Name = "OpeningRadius",
            Set = (o, v) => o.OpeningRadius = (int)v,
            Candidates = new double[] { 0, 1, 2, 3, 4, 6 },
        },
        new ParamSpec
        {
            Name = "ClosingRadius",
            Set = (o, v) => o.ClosingRadius = (int)v,
            Candidates = new double[] { 0, 2, 4, 6, 10, 15 },
        },
        new ParamSpec
        {
            Name = "MinStripeWidthPx",
            Set = (o, v) => o.MinStripeWidthPx = (int)v,
            Candidates = new double[] { 1, 3, 5, 8, 12, 18 },
        },
        new ParamSpec
        {
            Name = "MaxStripeWidthPx",
            Set = (o, v) => o.MaxStripeWidthPx = (int)v,
            Candidates = new double[] { 10, 20, 40, 60, 100, 160 },
        },
        new ParamSpec
        {
            Name = "MinStripeLengthPx",
            Set = (o, v) => o.MinStripeLengthPx = (int)v,
            Candidates = new double[] { 10, 20, 40, 60, 100, 150 },
        },
        new ParamSpec
        {
            Name = "MaxStripeLengthPx",
            Set = (o, v) => o.MaxStripeLengthPx = (int)v,
            Candidates = new double[] { 100, 200, 400, 700, 1200, 2000 },
        },
        new ParamSpec
        {
            Name = "MinStripePixels",
            Set = (o, v) => o.MinStripePixels = (int)v,
            Candidates = new double[] { 30, 60, 100, 200, 400, 800 },
        },
        new ParamSpec
        {
            Name = "MinAspectRatio",
            Set = (o, v) => o.MinAspectRatio = v,
            Candidates = new double[] { 1.5, 2.0, 2.5, 3.5, 5.0, 8.0 },
        },
        new ParamSpec
        {
            Name = "MinShortToLongRatio",
            Set = (o, v) => o.MinShortToLongRatio = v,
            Candidates = new double[] { 0.1, 0.3, 0.5, 0.7, 1.0, 1.3 },
        },
    };
}
