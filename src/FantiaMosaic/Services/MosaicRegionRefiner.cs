using System;
using System.Collections.Generic;
using FantiaMosaic.Models;
using SkiaSharp;

namespace FantiaMosaic.Services;

/// <summary>
/// 検出された粗いモザイク領域(シード)を、周囲の「線画的特徴のない」ピクセルへ
/// flood fill で拡張し、より正確な境界を求めるリファイナ。
///
/// 想定するモザイク特性:
///   - 中央: 強モザイク (タイル/ベタ)
///   - 周辺: 弱モザイク (薄ぼかし、ピンク縁取り等)
///   - 共通: 細線(1-2px)が消えている
/// 一方の自然画像 (キャラ輪郭、髪、目、背景の境界) には細線が残る。
/// 細線の有無を Laplacian-of-Gaussian 風の高周波応答で測り、応答の小さい連続領域を
/// 「モザイクが掛かっている領域」として取り出す。
/// </summary>
public sealed class MosaicRegionRefiner
{
    public sealed class Options
    {
        /// <summary>細線判定の高周波応答しきい値 (0-255)。小さいほど敏感に線画扱いし停止しやすい。</summary>
        public double LineEnergyThreshold { get; set; } = 8.0;
        /// <summary>シードからの拡張上限の絶対値 (px)。0で <see cref="MaxExpandRatio"/> のみ使用。</summary>
        public int MaxExpandPx { get; set; } = 0;
        /// <summary>シード短辺に対する拡張上限の割合。</summary>
        public double MaxExpandRatio { get; set; } = 0.5;
        /// <summary>シードを膨らませる初期マージン (px)。</summary>
        public int SeedPaddingPx { get; set; } = 4;
        /// <summary>処理を高速化するためのダウンスケール係数 (1=なし)。</summary>
        public int Downsample { get; set; } = 2;
        /// <summary>
        /// 拡張時に通過する非線画ピクセルの色多様性下限。
        /// シード周りの単色領域（肌・体など）への侵食を防ぐ。0で無効。
        /// </summary>
        public double LocalDiversityMin { get; set; } = 6.0;
    }

    /// <summary>
    /// シードを線画境界まで拡張し、得られた領域から最小面積回転矩形を再計算して返す。
    /// </summary>
    public RotatedMosaicDetector.OrientedRect Refine(
        SKBitmap bitmap,
        RotatedMosaicDetector.OrientedRect seed,
        Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var opt = options ?? new Options();
        var scale = Math.Max(1, opt.Downsample);

        // 1. グレースケール輝度を縮小サイズで取得
        var w = bitmap.Width / scale;
        var h = bitmap.Height / scale;
        var luma = new float[h, w];
        var pixels = bitmap.Pixels;
        var origW = bitmap.Width;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = pixels[(y * scale) * origW + (x * scale)];
            luma[y, x] = (float)(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue);
        }

        // 2. 細線エネルギーを計算 (元画像 - 軽いガウシアン)
        //    細い線は |高周波成分| が大きく、ぼかし/ベタは小さい
        var blurred = GaussianBlur(luma, h, w, sigma: 1.4f);
        var lineEnergy = new float[h, w];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            lineEnergy[y, x] = Math.Abs(luma[y, x] - blurred[y, x]);

        // 3. シード矩形を内側に取得し、外周に向かって flood fill 拡張
        var inSeed = RasterizeSeed(seed, scale, w, h, opt.SeedPaddingPx / scale);
        var insideRegion = new bool[h, w];
        var queue = new Queue<(int x, int y)>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            if (inSeed[y, x])
            {
                insideRegion[y, x] = true;
                queue.Enqueue((x, y));
            }

        // シードの短辺に対する比率で拡張上限を決める (絶対値 MaxExpandPx 指定時はそちら優先)
        var seedShort = Math.Min(seed.Width, seed.Height);
        var maxExpandActual = opt.MaxExpandPx > 0
            ? opt.MaxExpandPx
            : (int)Math.Round(seedShort * opt.MaxExpandRatio);
        var maxExpandScaled = Math.Max(1, maxExpandActual / scale);
        // 各ピクセルのシードからの距離 (BFS hop count)
        var dist = new int[h, w];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                dist[y, x] = inSeed[y, x] ? 0 : -1;

        // 局所色多様性マップ (各ピクセル周辺 7x7 の輝度標準偏差)
        // 「線画はないけど完全な単色面」(肌や白背景) はこれが低い → 拡張停止
        var diversityMap = (opt.LocalDiversityMin > 0)
            ? ComputeLocalDiversity(luma, h, w, radius: 3)
            : null;

        var thresh = (float)opt.LineEnergyThreshold;
        var divMin = (float)opt.LocalDiversityMin;
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            var d = dist[y, x];
            if (d >= maxExpandScaled) continue;
            foreach (var (nx, ny) in Neighbors4(x, y, w, h))
            {
                if (insideRegion[ny, nx]) continue;
                if (lineEnergy[ny, nx] > thresh) continue; // 線画にぶつかったら停止
                if (diversityMap != null && diversityMap[ny, nx] < divMin) continue; // 単色面で停止
                insideRegion[ny, nx] = true;
                dist[ny, nx] = d + 1;
                queue.Enqueue((nx, ny));
            }
        }

        // 4. 拡張後の領域の全境界点を集める
        var pts = new List<SKPoint>();
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (!insideRegion[y, x]) continue;
            var isEdge = (x == 0 || y == 0 || x == w - 1 || y == h - 1)
                      || !insideRegion[y, x - 1] || !insideRegion[y, x + 1]
                      || !insideRegion[y - 1, x] || !insideRegion[y + 1, x];
            if (isEdge)
                pts.Add(new SKPoint(x * scale, y * scale));
        }
        if (pts.Count < 4)
            return seed;

        // 拡張が非対称になりがちなため、シード中心を起点に対称化する。
        // 各点 (dx, dy) に対して (-dx, -dy) も同時に含めることで、
        // 結果の矩形がシード中心を中心とした左右対称な向きになる。
        var symPts = new List<SKPoint>(pts.Count * 2);
        foreach (var p in pts)
        {
            var dx = p.X - seed.Center.X;
            var dy = p.Y - seed.Center.Y;
            symPts.Add(new SKPoint(seed.Center.X + dx, seed.Center.Y + dy));
            symPts.Add(new SKPoint(seed.Center.X - dx, seed.Center.Y - dy));
        }

        var rect = RotatedMosaicDetector.MinAreaRectPublic(symPts);
        // 中心はシードに合わせる (シードが既にモザイク領域の真ん中を取れているという前提)
        return new RotatedMosaicDetector.OrientedRect(seed.Center, rect.Width, rect.Height, rect.AngleRad);
    }

    public IReadOnlyList<MosaicRegion> RefineAsRegions(
        SKBitmap bitmap,
        IReadOnlyList<RotatedMosaicDetector.OrientedRect> seeds,
        Options? options = null)
    {
        var list = new List<MosaicRegion>(seeds.Count);
        foreach (var seed in seeds)
        {
            var refined = Refine(bitmap, seed, options);
            list.Add(MosaicRegion.FromPolygon(refined.Vertices().ToArray()));
        }
        return list;
    }

    // --- helpers ---

    private static bool[,] RasterizeSeed(RotatedMosaicDetector.OrientedRect seed, int scale, int w, int h, int paddingBlocks)
    {
        // シードを少しだけ縮小スケールでラスタライズ
        var verts = seed.Vertices();
        var mask = new bool[h, w];

        // シードを膨らませる場合は中心を起点に拡大
        var c = MathF.Cos(seed.AngleRad);
        var s = MathF.Sin(seed.AngleRad);
        var hw = (seed.Width / 2f + paddingBlocks * scale) / scale;
        var hh = (seed.Height / 2f + paddingBlocks * scale) / scale;
        var cx = seed.Center.X / scale;
        var cy = seed.Center.Y / scale;

        // AABB を求めて走査
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var v in new[]
        {
            (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh),
        })
        {
            var x = v.Item1 * c - v.Item2 * s + cx;
            var y = v.Item1 * s + v.Item2 * c + cy;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
        var l = Math.Max(0, (int)minX);
        var t = Math.Max(0, (int)minY);
        var r = Math.Min(w, (int)Math.Ceiling(maxX));
        var b = Math.Min(h, (int)Math.Ceiling(maxY));

        for (var y = t; y < b; y++)
        for (var x = l; x < r; x++)
        {
            // ローカル座標に逆回転
            var dx = x - cx;
            var dy = y - cy;
            var lx = dx * c + dy * s;
            var ly = -dx * s + dy * c;
            if (Math.Abs(lx) <= hw && Math.Abs(ly) <= hh)
                mask[y, x] = true;
        }
        return mask;
    }

    private static IEnumerable<(int x, int y)> Neighbors4(int x, int y, int w, int h)
    {
        if (x > 0) yield return (x - 1, y);
        if (y > 0) yield return (x, y - 1);
        if (x < w - 1) yield return (x + 1, y);
        if (y < h - 1) yield return (x, y + 1);
    }

    private static float[,] ComputeLocalDiversity(float[,] src, int h, int w, int radius)
    {
        var result = new float[h, w];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            double sum = 0, sumSq = 0;
            var n = 0;
            var y0 = Math.Max(0, y - radius);
            var y1 = Math.Min(h - 1, y + radius);
            var x0 = Math.Max(0, x - radius);
            var x1 = Math.Min(w - 1, x + radius);
            for (var yy = y0; yy <= y1; yy++)
            for (var xx = x0; xx <= x1; xx++)
            {
                var v = src[yy, xx];
                sum += v;
                sumSq += v * v;
                n++;
            }
            var mean = sum / n;
            var variance = sumSq / n - mean * mean;
            result[y, x] = (float)Math.Sqrt(Math.Max(0, variance));
        }
        return result;
    }

    private static float[,] GaussianBlur(float[,] src, int h, int w, float sigma)
    {
        var radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var kernel = new float[2 * radius + 1];
        var sum = 0f;
        for (var i = -radius; i <= radius; i++)
        {
            var v = MathF.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = v;
            sum += v;
        }
        for (var i = 0; i < kernel.Length; i++) kernel[i] /= sum;

        var tmp = new float[h, w];
        // 水平
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var acc = 0f;
            for (var k = -radius; k <= radius; k++)
            {
                var xi = Math.Clamp(x + k, 0, w - 1);
                acc += src[y, xi] * kernel[k + radius];
            }
            tmp[y, x] = acc;
        }
        // 垂直
        var dst = new float[h, w];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var acc = 0f;
            for (var k = -radius; k <= radius; k++)
            {
                var yi = Math.Clamp(y + k, 0, h - 1);
                acc += tmp[yi, x] * kernel[k + radius];
            }
            dst[y, x] = acc;
        }
        return dst;
    }
}
