using System;
using System.Collections.Generic;
using System.Linq;
using FantiaMosaic.Models;
using SkiaSharp;

namespace FantiaMosaic.Services;

/// <summary>
/// 「タイルモザイクは規則的な格子状エッジを持つ」性質を使い、
/// 格子テンプレートとの正規化相互相関 (NCC) でモザイク領域を検出する。
///
/// 参考: https://qiita.com/summer4an/items/306acc5d38169f880ba8
/// 同記事の Python/OpenCV (matchTemplate, TM_CCOEFF_NORMED) を SkiaSharp 用に移植。
///
/// 主要処理:
///   1. グレースケール + 軽いガウシアン
///   2. Sobel 振幅でエッジ強度マップ
///   3. 複数の格子テンプレート (TileSizes) を生成し、正規化相互相関を計算
///   4. しきい値で二値化
///   5. 膨張 → 連結成分 → 最小面積回転矩形
/// </summary>
public sealed class MosaicGridDetector
{
    public sealed class Options
    {
        /// <summary>マッチに使うタイルサイズ候補 (px、原寸基準)。</summary>
        public int[] TileSizes { get; set; } = new[] { 4, 6, 8, 10, 12, 16, 20 };
        /// <summary>テンプレートに含める格子の繰り返し数 (テンプレート一辺 = TileSize × Repeats)。</summary>
        public int TemplateRepeats { get; set; } = 3;
        /// <summary>NCC のしきい値 (0-1)。これ以上をモザイク候補とする。</summary>
        public double MatchThreshold { get; set; } = 0.32;
        /// <summary>処理高速化のためのダウンサンプル係数。</summary>
        public int Downsample { get; set; } = 2;
        /// <summary>結果マスクのモルフォロジー膨張半径 (原寸 px)。</summary>
        public int DilationRadius { get; set; } = 4;
        /// <summary>最小成分面積比 (画像全体に対する)。</summary>
        public double MinAreaRatio { get; set; } = 0.003;
        /// <summary>除外する画像端マージン比 (0.05 = 上下左右 5% を無視)。エッジアーティファクト対策。</summary>
        public double BorderIgnoreRatio { get; set; } = 0.02;
        /// <summary>
        /// 検出した連結成分から矩形を求めるアルゴリズム。
        /// true=PCA (主軸ベース、傾き精度が安定)、false=MinAreaRect (最小面積、外乱に敏感)。
        /// </summary>
        public bool UsePca { get; set; } = true;
        /// <summary>
        /// 高しきい値に対する低しきい値の比 (0-1)。薄モザイク部分も拾うために使う。
        /// </summary>
        public double LowThresholdRatio { get; set; } = 0.8;
        /// <summary>
        /// 連結成分のAABBスパンに対し低マスク再収集する近傍範囲の比率。
        /// </summary>
        public double LowRegionExpandRatio { get; set; } = 0.05;
        /// <summary>
        /// 最終結果の矩形に追加する安全マージン (短辺比)。
        /// 0.15 なら短辺の15%を四方に追加。
        /// </summary>
        public double SafetyMarginRatio { get; set; } = 0.0;
        /// <summary>
        /// 「白背景」と判定する輝度しきい値 (0-255)。サイズ判定でこれを超えるピクセルは除外。
        /// </summary>
        public double WhiteBackgroundLumaThreshold { get; set; } = 240.0;
    }

    public IReadOnlyList<RotatedMosaicDetector.OrientedRect> Detect(SKBitmap bitmap, Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var opt = options ?? new Options();
        var scale = Math.Max(1, opt.Downsample);

        var w = bitmap.Width / scale;
        var h = bitmap.Height / scale;
        if (w < 32 || h < 32) return Array.Empty<RotatedMosaicDetector.OrientedRect>();

        // 1. グレースケール輝度
        var luma = new float[h * w];
        var pixels = bitmap.Pixels;
        var origW = bitmap.Width;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = pixels[(y * scale) * origW + (x * scale)];
            luma[y * w + x] = (float)(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue);
        }

        // 2. Sobel 振幅 (エッジ画像)
        var edge = SobelMagnitude(luma, h, w);

        // 3. 各テンプレートサイズで NCC マップを計算、最大値を取る
        var bestScore = new float[h * w];
        foreach (var rawT in opt.TileSizes)
        {
            var T = Math.Max(2, rawT / scale);
            var tplSize = T * opt.TemplateRepeats;
            if (tplSize >= Math.Min(w, h) / 2) continue;

            var template = BuildGridTemplate(T, opt.TemplateRepeats);
            var ncc = MatchTemplateNcc(edge, h, w, template, tplSize, tplSize);
            for (var i = 0; i < bestScore.Length; i++)
                if (ncc[i] > bestScore[i]) bestScore[i] = ncc[i];
        }

        // 4. しきい値でマスク化 (高/低 2段階)
        // - 高しきい値: 確実なモザイクピクセル (シード)
        // - 低しきい値: 候補ピクセル (薄モザイクや境界部分も含む)
        var hiThresh = (float)opt.MatchThreshold;
        var loThresh = (float)(opt.MatchThreshold * opt.LowThresholdRatio);
        var border = (int)(Math.Max(w, h) * opt.BorderIgnoreRatio);
        var maskHi = new bool[h, w];
        var maskLo = new bool[h, w];
        for (var y = border; y < h - border; y++)
        for (var x = border; x < w - border; x++)
        {
            var s = bestScore[y * w + x];
            if (s >= hiThresh) maskHi[y, x] = true;
            if (s >= loThresh) maskLo[y, x] = true;
        }

        // 5. 膨張 (Hi マスクで連結性を確保)
        if (opt.DilationRadius > 0)
            maskHi = DilatePixels(maskHi, h, w, Math.Max(1, opt.DilationRadius / scale));

        // 6. 連結成分 → 回転矩形 (PCA or MinAreaRect)
        var minArea = Math.Max(8, (int)(w * h * opt.MinAreaRatio));
        var components = ConnectedComponents(maskHi, h, w);
        var results = new List<RotatedMosaicDetector.OrientedRect>();
        foreach (var comp in components)
        {
            if (comp.Count < minArea) continue;

            // 連結成分の AABB を取り、その近傍 (kRadius ブロック分) で
            // 低しきい値を超えるピクセルも領域に含める (PCA で長軸方向を安定化させる用)。
            int aL = int.MaxValue, aT = int.MaxValue, aR = int.MinValue, aB = int.MinValue;
            foreach (var (x, y) in comp)
            {
                if (x < aL) aL = x;
                if (y < aT) aT = y;
                if (x > aR) aR = x;
                if (y > aB) aB = y;
            }
            var span = Math.Max(aR - aL, aB - aT);
            var kRadius = Math.Max(1, (int)(span * opt.LowRegionExpandRatio));
            var lx = Math.Max(0, aL - kRadius);
            var ly = Math.Max(0, aT - kRadius);
            var ux = Math.Min(w, aR + kRadius);
            var uy = Math.Min(h, aB + kRadius);

            // 角度推定用 (広範囲、低スコアも含む)
            var anglePts = new List<SKPoint>();
            var angleWeights = new List<float>();
            for (var y = ly; y < uy; y++)
            for (var x = lx; x < ux; x++)
            {
                if (!maskLo[y, x]) continue;
                anglePts.Add(new SKPoint(x * scale, y * scale));
                angleWeights.Add(bestScore[y * w + x]);
            }

            // サイズ決定用: 低マスク広範囲 ∩ 「非白背景」のピクセル。
            // ・低マスクで札のピンク薄モザイク部まで広く取る → 矩形が札全体を覆う
            // ・白い余白を除外 → 下の余白に矩形が伸びるのを防ぐ
            var sizePts = new List<SKPoint>();
            for (var y = ly; y < uy; y++)
            for (var x = lx; x < ux; x++)
            {
                if (!maskLo[y, x]) continue;
                var c = pixels[(y * scale) * origW + (x * scale)];
                var L = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                if (L > opt.WhiteBackgroundLumaThreshold) continue; // ほぼ白 → 背景とみなして除外
                sizePts.Add(new SKPoint(x * scale, y * scale));
            }
            if (sizePts.Count < 8)
            {
                // 非白ピクセルが極端に少ない場合はフォールバック (シードのみ)
                sizePts.Clear();
                foreach (var (x, y) in comp)
                    sizePts.Add(new SKPoint(x * scale, y * scale));
            }

            // 1) 角度は広範囲PCAで取る
            double theta;
            if (opt.UsePca && anglePts.Count >= 3)
            {
                var angleRect = RotatedMosaicDetector.WeightedPcaRect(anglePts, angleWeights);
                theta = angleRect.AngleRad;
            }
            else
            {
                var fallback = RotatedMosaicDetector.MinAreaRectPublic(sizePts);
                theta = fallback.AngleRad;
            }

            // 2) サイズ・中心はその角度の座標系で「シード点」の AABB から決める
            var cosT = Math.Cos(theta);
            var sinT = Math.Sin(theta);
            double mU = 0, mV = 0;
            foreach (var p in sizePts)
            {
                mU += p.X * cosT + p.Y * sinT;
                mV += -p.X * sinT + p.Y * cosT;
            }
            mU /= sizePts.Count;
            mV /= sizePts.Count;
            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;
            foreach (var p in sizePts)
            {
                var u = p.X * cosT + p.Y * sinT;
                var v = -p.X * sinT + p.Y * cosT;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
            var rectW = (float)(maxU - minU);
            var rectH = (float)(maxV - minV);
            var cu = (minU + maxU) / 2;
            var cv = (minV + maxV) / 2;
            var cx = (float)(cu * cosT - cv * sinT);
            var cy = (float)(cu * sinT + cv * cosT);
            var rect = new RotatedMosaicDetector.OrientedRect(new SKPoint(cx, cy), rectW, rectH, (float)theta);

            // 安全マージンを追加（薄モザイク縁取りを取りこぼさないため）
            if (opt.SafetyMarginRatio > 0)
            {
                var shortSide = Math.Min(rect.Width, rect.Height);
                var pad = (float)(shortSide * opt.SafetyMarginRatio);
                rect = new RotatedMosaicDetector.OrientedRect(
                    rect.Center,
                    rect.Width + pad * 2,
                    rect.Height + pad * 2,
                    rect.AngleRad);
            }
            results.Add(rect);
        }
        return results.OrderByDescending(r => r.Width * r.Height).ToList();
    }

    public IReadOnlyList<MosaicRegion> DetectAsRegions(SKBitmap bitmap, Options? options = null)
        => Detect(bitmap, options)
            .Select(r => MosaicRegion.FromOrientedRect(r.Center, r.Width, r.Height, r.AngleRad))
            .ToList();

    /// <summary>
    /// 内部スコアマップを画像として可視化する (デバッグ用)。
    /// </summary>
    public SKBitmap BuildScoreMap(SKBitmap bitmap, Options? options = null)
    {
        var opt = options ?? new Options();
        var scale = Math.Max(1, opt.Downsample);
        var w = bitmap.Width / scale;
        var h = bitmap.Height / scale;

        var luma = new float[h * w];
        var pixels = bitmap.Pixels;
        var origW = bitmap.Width;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = pixels[(y * scale) * origW + (x * scale)];
            luma[y * w + x] = (float)(0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue);
        }
        var edge = SobelMagnitude(luma, h, w);
        var best = new float[h * w];
        foreach (var rawT in opt.TileSizes)
        {
            var T = Math.Max(2, rawT / scale);
            var tplSize = T * opt.TemplateRepeats;
            if (tplSize >= Math.Min(w, h) / 2) continue;
            var template = BuildGridTemplate(T, opt.TemplateRepeats);
            var ncc = MatchTemplateNcc(edge, h, w, template, tplSize, tplSize);
            for (var i = 0; i < best.Length; i++)
                if (ncc[i] > best[i]) best[i] = ncc[i];
        }
        var bmp = new SKBitmap(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var v = Math.Clamp(best[y * w + x], 0, 1);
            var g = (byte)(v * 255);
            bmp.SetPixel(x, y, new SKColor(g, g, g));
        }
        return bmp;
    }

    // ==================== Internals ====================

    /// <summary>
    /// 1次元ガウシアンを掛けない、純粋な Sobel x/y の振幅マップ。
    /// </summary>
    private static float[] SobelMagnitude(float[] src, int h, int w)
    {
        var dst = new float[h * w];
        for (var y = 1; y < h - 1; y++)
        for (var x = 1; x < w - 1; x++)
        {
            var i = y * w + x;
            var im1 = (y - 1) * w + x;
            var ip1 = (y + 1) * w + x;
            var gx = -src[im1 - 1] + src[im1 + 1]
                   - 2 * src[i - 1] + 2 * src[i + 1]
                   - src[ip1 - 1] + src[ip1 + 1];
            var gy = -src[im1 - 1] - 2 * src[im1] - src[im1 + 1]
                   + src[ip1 - 1] + 2 * src[ip1] + src[ip1 + 1];
            dst[i] = MathF.Sqrt(gx * gx + gy * gy);
        }
        return dst;
    }

    /// <summary>
    /// TxT の正方形を repeats 回繰り返した格子パターンを生成する。
    /// 周期 T で水平/垂直の線が引かれた画像。線は 1px 幅、値=1。それ以外は 0。
    /// </summary>
    private static float[] BuildGridTemplate(int T, int repeats)
    {
        var size = T * repeats;
        var t = new float[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            // 周期 T の格子線
            if (y % T == 0 || x % T == 0)
                t[y * size + x] = 1f;
        }
        return t;
    }

    /// <summary>
    /// OpenCV TM_CCOEFF_NORMED と同等の正規化相互相関マップを計算する。
    /// 結果は image の中心位置 (cy, cx) に格納される（template の左上が (cy - h/2, cx - w/2)）。
    /// 高速化のため積分画像 (Σ I) と (Σ I²) を使う。
    /// </summary>
    private static float[] MatchTemplateNcc(
        float[] image, int h, int w,
        float[] template, int th, int tw)
    {
        // テンプレートの平均と中心化テンプレート、ノルム
        var tArea = th * tw;
        double sumT = 0;
        for (var i = 0; i < tArea; i++) sumT += template[i];
        var meanT = (float)(sumT / tArea);
        var tCentered = new float[tArea];
        double sumTSq = 0;
        for (var i = 0; i < tArea; i++)
        {
            var v = template[i] - meanT;
            tCentered[i] = v;
            sumTSq += v * v;
        }
        var normT = (float)Math.Sqrt(sumTSq);

        // image の積分画像 (Σ I, Σ I²)
        // sat[y, x] = Σ_{j<y, i<x} image[j, i] (1-indexed)
        var sat = new double[(h + 1) * (w + 1)];
        var satSq = new double[(h + 1) * (w + 1)];
        for (var y = 1; y <= h; y++)
        {
            double rowSum = 0, rowSumSq = 0;
            for (var x = 1; x <= w; x++)
            {
                var v = image[(y - 1) * w + (x - 1)];
                rowSum += v;
                rowSumSq += v * v;
                sat[y * (w + 1) + x] = sat[(y - 1) * (w + 1) + x] + rowSum;
                satSq[y * (w + 1) + x] = satSq[(y - 1) * (w + 1) + x] + rowSumSq;
            }
        }

        double WindowSum(double[] s, int y1, int x1, int y2, int x2)
        {
            // window [y1..y2-1, x1..x2-1]、半開区間
            return s[y2 * (w + 1) + x2]
                 - s[y1 * (w + 1) + x2]
                 - s[y2 * (w + 1) + x1]
                 + s[y1 * (w + 1) + x1];
        }

        var result = new float[h * w];
        var halfH = th / 2;
        var halfW = tw / 2;

        // テンプレート左上座標 (x0, y0) を走査
        for (var y0 = 0; y0 <= h - th; y0++)
        for (var x0 = 0; x0 <= w - tw; x0++)
        {
            var sumI = WindowSum(sat, y0, x0, y0 + th, x0 + tw);
            var sumISq = WindowSum(satSq, y0, x0, y0 + th, x0 + tw);
            var meanI = sumI / tArea;
            var varI = sumISq - tArea * meanI * meanI;
            if (varI <= 1e-6)
            {
                continue; // 完全に均一 = エッジ無し
            }
            var normI = Math.Sqrt(varI);

            // 分子: Σ (I - meanI) * tCentered = Σ I * tCentered - meanI * Σ tCentered (=0)
            // tCentered の総和は 0 なので、Σ I * tCentered だけで OK
            double dot = 0;
            for (var ty = 0; ty < th; ty++)
            {
                var imageRow = (y0 + ty) * w + x0;
                var tplRow = ty * tw;
                for (var tx = 0; tx < tw; tx++)
                    dot += image[imageRow + tx] * tCentered[tplRow + tx];
            }
            var ncc = (float)(dot / (normI * normT + 1e-9));

            // 結果はテンプレート中心位置に格納
            var cy = y0 + halfH;
            var cx = x0 + halfW;
            result[cy * w + cx] = ncc;
        }
        return result;
    }

    private static bool[,] DilatePixels(bool[,] src, int h, int w, int radius)
    {
        var current = src;
        for (var iter = 0; iter < radius; iter++)
        {
            var next = new bool[h, w];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (current[y, x]) { next[y, x] = true; continue; }
                if ((x > 0 && current[y, x - 1])
                 || (x < w - 1 && current[y, x + 1])
                 || (y > 0 && current[y - 1, x])
                 || (y < h - 1 && current[y + 1, x]))
                    next[y, x] = true;
            }
            current = next;
        }
        return current;
    }

    private static List<List<(int x, int y)>> ConnectedComponents(bool[,] mask, int h, int w)
    {
        var labels = new int[h, w];
        var comps = new List<List<(int x, int y)>>();
        var next = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (!mask[y, x] || labels[y, x] != 0) continue;
            next++;
            var comp = new List<(int x, int y)>();
            var stack = new Stack<(int x, int y)>();
            stack.Push((x, y));
            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Pop();
                if ((uint)cx >= (uint)w || (uint)cy >= (uint)h) continue;
                if (!mask[cy, cx] || labels[cy, cx] != 0) continue;
                labels[cy, cx] = next;
                comp.Add((cx, cy));
                stack.Push((cx + 1, cy));
                stack.Push((cx - 1, cy));
                stack.Push((cx, cy + 1));
                stack.Push((cx, cy - 1));
            }
            comps.Add(comp);
        }
        return comps;
    }
}
