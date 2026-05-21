using System;
using System.Collections.Generic;
using System.Linq;
using FantiaMosaic.Models;
using SkiaSharp;

namespace FantiaMosaic.Services;

/// <summary>
/// 「黒い棒線(ストライプ)による部分的隠蔽」領域を検出する。
///
/// Fantia 2026-05-25 改定ガイドラインで明示的に NG とされた手法を、
/// ガイドライン適合モザイクで上書きするために使う。
///
/// アルゴリズム:
///   1. 黒ピクセルを抽出
///   2. 連結成分のうち、PCA で計算した長軸/短軸比が大きい(=細長い)ものを「棒線候補」に
///   3. 棒線候補の中で、近接 & 平行 のものをグルーピング
///   4. 各グループの全黒ピクセルから長軸方向の外接矩形を求めて返す
/// </summary>
public sealed class StripeOcclusionDetector
{
    public sealed class Options
    {
        /// <summary>黒ピクセル判定しきい値 (輝度 0-255)。</summary>
        public int DarkLumaThreshold { get; set; } = 100;
        /// <summary>棒線候補とみなす最小アスペクト比 (長辺/短辺)。</summary>
        public double MinAspectRatio { get; set; } = 2.5;
        /// <summary>棒線候補の最小ピクセル数。</summary>
        public int MinStripePixels { get; set; } = 80;
        /// <summary>棒線候補の最小長辺 (px)。短すぎる線分を除外。</summary>
        public int MinStripeLengthPx { get; set; } = 30;
        /// <summary>棒線候補の最小幅 (短辺、px)。細い線画やハーフトーンドットを除外。</summary>
        public int MinStripeWidthPx { get; set; } = 4;
        /// <summary>棒線候補の最大幅 (短辺、px)。太すぎるベタ塗り領域を除外。</summary>
        public int MaxStripeWidthPx { get; set; } = 40;
        /// <summary>棒線候補の最大長辺 (px)。これを超える長い領域は巨大ベタとみなして除外。</summary>
        public int MaxStripeLengthPx { get; set; } = 600;
        /// <summary>棒線候補の最大ピクセル数。これより大きいと「ベタ塗り」や線画とみなして除外。</summary>
        public int MaxStripePixels { get; set; } = 100000;
        /// <summary>グルーピングの角度差許容 (度)。</summary>
        public double GroupAngleToleranceDeg { get; set; } = 25.0;
        /// <summary>グルーピングの距離許容 = (各棒線の短辺平均) × この係数。</summary>
        public double GroupDistanceShortSideMul { get; set; } = 15.0;
        /// <summary>1グループに必要な最小棒線数。1なら単独棒線も検出。</summary>
        public int MinStripesPerGroup { get; set; } = 1;
        /// <summary>ダウンサンプル係数。</summary>
        public int Downsample { get; set; } = 1;
        /// <summary>結果矩形に追加する短辺比のマージン。</summary>
        public double SafetyMarginRatio { get; set; } = 0.05;
        /// <summary>
        /// 短辺方向への最小サイズ (長辺に対する比率)。
        /// 棒線隠蔽の場合、棒線が並ぶ方向 (短辺方向) は元々狭いが、隠されてる被写体本体は
        /// それより大きいことが多い。この比率で長辺サイズに合わせて短辺方向を拡張する。
        /// 1.0 なら短辺=長辺の正方形まで膨らむ。
        /// </summary>
        public double MinShortToLongRatio { get; set; } = 0.7;
        /// <summary>黒マスクに掛けるモルフォロジカル膨張半径。0=無効。</summary>
        public int DilationRadius { get; set; } = 0;
        /// <summary>黒マスクに掛けるモルフォロジカル opening 半径 (細い線画を除去)。
        /// opening = erode → dilate。これにより指定半径未満の細い線が消え、太い帯だけが残る。</summary>
        public int OpeningRadius { get; set; } = 2;
        /// <summary>
        /// 黒マスクに掛けるモルフォロジカル closing 半径 (棒線上の装飾を吸収)。
        /// closing = dilate → erode。棒線の上にハート・汗・効果線等が乗って分断された場合に
        /// それらの隙間を埋めて1つの塊にする。0=無効。
        /// 大きすぎると別物の棒線同士まで結合されるため UI で画像に合わせて調整推奨。
        /// </summary>
        public int ClosingRadius { get; set; } = 0;
    }

    public IReadOnlyList<RotatedMosaicDetector.OrientedRect> Detect(SKBitmap bitmap, Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var opt = options ?? new Options();
        var scale = Math.Max(1, opt.Downsample);

        var w = bitmap.Width / scale;
        var h = bitmap.Height / scale;
        if (w < 16 || h < 16) return Array.Empty<RotatedMosaicDetector.OrientedRect>();

        // 1. 黒ピクセル抽出
        var pixels = bitmap.Pixels;
        var origW = bitmap.Width;
        var isDark = new bool[h, w];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var c = pixels[(y * scale) * origW + (x * scale)];
            var L = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
            if (L < opt.DarkLumaThreshold) isDark[y, x] = true;
        }

        // 1b. OpenClose 前処理:
        //   ① Opening (erode→dilate): 細い線画・ハーフトーンドット・効果線などを除去
        //   ② Closing (dilate→erode): 装飾で分断された本物の棒線の隙間を埋めて1塊にする
        // 順序が重要 — 先に細線を消してから、残った太い棒線の連結を回復する。
        if (opt.OpeningRadius > 0)
        {
            isDark = Erode(isDark, h, w, opt.OpeningRadius);
            isDark = Dilate(isDark, h, w, opt.OpeningRadius);
        }
        if (opt.ClosingRadius > 0)
        {
            isDark = Dilate(isDark, h, w, opt.ClosingRadius);
            isDark = Erode(isDark, h, w, opt.ClosingRadius);
        }
        // 1c. 軽い膨張で断片化を防ぐ
        if (opt.DilationRadius > 0)
            isDark = Dilate(isDark, h, w, opt.DilationRadius);

        // 2. 連結成分
        var components = ConnectedComponents4(isDark, h, w);
        var diag = new Diagnostics { TotalComponents = components.Count };

        // 3. 細長い棒線候補を抽出
        var stripes = new List<StripeInfo>();
        var maxLenAbs = (double)opt.MaxStripeLengthPx;
        foreach (var comp in components)
        {
            if (comp.Count < opt.MinStripePixels || comp.Count > opt.MaxStripePixels)
            {
                diag.SizeRejected++;
                continue;
            }
            var info = AnalyzeStripe(comp, scale);
            if (info.Width < 1e-3f) { diag.AspectRejected++; continue; }
            var aspect = info.Length / info.Width;
            if (aspect < opt.MinAspectRatio) { diag.AspectRejected++; continue; }
            if (info.Length < opt.MinStripeLengthPx) { diag.AspectRejected++; continue; }
            if (info.Length > maxLenAbs) { diag.AspectRejected++; continue; } // 巨大すぎ
            if (info.Width < opt.MinStripeWidthPx) { diag.AspectRejected++; continue; } // 細すぎ (線画やドット)
            if (info.Width > opt.MaxStripeWidthPx) { diag.AspectRejected++; continue; } // 太すぎ (ベタ塗り)
            stripes.Add(info);
            diag.StripeDetails.Add((comp.Count, info.Length, info.Width, aspect, info.AngleRad * 180 / Math.PI));
        }
        diag.StripesAccepted = stripes.Count;

        if (stripes.Count == 0)
        {
            LastDiagnostics = diag;
            return Array.Empty<RotatedMosaicDetector.OrientedRect>();
        }

        // 4. 平行 & 近接でグルーピング
        var groups = ClusterStripes(stripes, opt);
        diag.GroupsCount = groups.Count;

        // 5. 各グループの外接矩形 (グループ平均角度の座標系で AABB)
        var results = new List<RotatedMosaicDetector.OrientedRect>();
        foreach (var group in groups)
        {
            if (group.Count < opt.MinStripesPerGroup) continue;
            diag.GroupsAccepted++;

            // 平均角度 (-90..90 で正規化してから平均)
            double sumA = 0;
            foreach (var s in group) sumA += NormalizeAngle(s.AngleRad);
            var meanAngle = sumA / group.Count;
            var cos = Math.Cos(meanAngle);
            var sin = Math.Sin(meanAngle);

            // 全棒線の全ピクセルから AABB
            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;
            foreach (var s in group)
            foreach (var p in s.Pixels)
            {
                var u = p.X * cos + p.Y * sin;
                var v = -p.X * sin + p.Y * cos;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
            var rectW = (float)(maxU - minU);
            var rectH = (float)(maxV - minV);
            var cu = (minU + maxU) / 2;
            var cv = (minV + maxV) / 2;
            var cx = (float)(cu * cos - cv * sin);
            var cy = (float)(cu * sin + cv * cos);
            var rect = new RotatedMosaicDetector.OrientedRect(new SKPoint(cx, cy), rectW, rectH, (float)meanAngle);

            // 短辺方向を被写体本体までカバーするよう拡張
            if (opt.MinShortToLongRatio > 0)
            {
                var longSide = Math.Max(rect.Width, rect.Height);
                var minShort = (float)(longSide * opt.MinShortToLongRatio);
                var newW = rect.Width;
                var newH = rect.Height;
                if (rect.Width < rect.Height)
                {
                    if (newW < minShort) newW = minShort;
                }
                else
                {
                    if (newH < minShort) newH = minShort;
                }
                rect = new RotatedMosaicDetector.OrientedRect(rect.Center, newW, newH, rect.AngleRad);
            }

            // 安全マージン
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

        LastDiagnostics = diag;
        return results.OrderByDescending(r => r.Width * r.Height).ToList();
    }

    public IReadOnlyList<MosaicRegion> DetectAsRegions(SKBitmap bitmap, Options? options = null)
        => Detect(bitmap, options)
            .Select(r => MosaicRegion.FromOrientedRect(r.Center, r.Width, r.Height, r.AngleRad))
            .ToList();

    public sealed class Diagnostics
    {
        public int TotalComponents;
        public int SizeRejected;
        public int AspectRejected;
        public int StripesAccepted;
        public List<(int Pixels, double Length, double Width, double AspectRatio, double AngleDeg)> StripeDetails = new();
        public int GroupsCount;
        public int GroupsAccepted;
    }

    public Diagnostics LastDiagnostics { get; private set; } = new();

    // ==================== Internals ====================

    private sealed class StripeInfo
    {
        public List<SKPoint> Pixels = new();
        public SKPoint Center;
        public double Length;
        public double Width;
        public double AngleRad;
    }

    private static StripeInfo AnalyzeStripe(List<(int x, int y)> blockPixels, int scale)
    {
        var pts = new List<SKPoint>(blockPixels.Count);
        foreach (var (x, y) in blockPixels)
            pts.Add(new SKPoint(x * scale, y * scale));

        double mx = 0, my = 0;
        foreach (var p in pts) { mx += p.X; my += p.Y; }
        mx /= pts.Count;
        my /= pts.Count;

        double cxx = 0, cyy = 0, cxy = 0;
        foreach (var p in pts)
        {
            var dx = p.X - mx;
            var dy = p.Y - my;
            cxx += dx * dx;
            cyy += dy * dy;
            cxy += dx * dy;
        }
        cxx /= pts.Count;
        cyy /= pts.Count;
        cxy /= pts.Count;

        var theta = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);

        double minU = double.MaxValue, maxU = double.MinValue;
        double minV = double.MaxValue, maxV = double.MinValue;
        foreach (var p in pts)
        {
            var dx = p.X - mx;
            var dy = p.Y - my;
            var u = dx * cos + dy * sin;
            var v = -dx * sin + dy * cos;
            if (u < minU) minU = u;
            if (u > maxU) maxU = u;
            if (v < minV) minV = v;
            if (v > maxV) maxV = v;
        }

        return new StripeInfo
        {
            Pixels = pts,
            Center = new SKPoint((float)mx, (float)my),
            Length = maxU - minU,
            Width = maxV - minV,
            AngleRad = theta,
        };
    }

    private static List<List<StripeInfo>> ClusterStripes(List<StripeInfo> stripes, Options opt)
    {
        var n = stripes.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int a, int b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }

        var angleTolRad = opt.GroupAngleToleranceDeg * Math.PI / 180.0;

        for (var i = 0; i < n; i++)
        for (var j = i + 1; j < n; j++)
        {
            // 角度差 (-90..90 で正規化)
            var ai = NormalizeAngle(stripes[i].AngleRad);
            var aj = NormalizeAngle(stripes[j].AngleRad);
            var da = Math.Abs(ai - aj);
            if (da > Math.PI / 2) da = Math.PI - da;
            if (da > angleTolRad) continue;

            // 距離: 中心間距離 が 棒線の短辺平均 × 係数 以内
            var dx = stripes[i].Center.X - stripes[j].Center.X;
            var dy = stripes[i].Center.Y - stripes[j].Center.Y;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            var avgShort = (stripes[i].Width + stripes[j].Width) / 2;
            if (dist > avgShort * opt.GroupDistanceShortSideMul) continue;

            Union(i, j);
        }

        var groupMap = new Dictionary<int, List<StripeInfo>>();
        for (var i = 0; i < n; i++)
        {
            var r = Find(i);
            if (!groupMap.TryGetValue(r, out var lst)) groupMap[r] = lst = new();
            lst.Add(stripes[i]);
        }
        return groupMap.Values.ToList();
    }

    private static double NormalizeAngle(double rad)
    {
        // -π/2 .. π/2 に正規化
        while (rad > Math.PI / 2) rad -= Math.PI;
        while (rad < -Math.PI / 2) rad += Math.PI;
        return rad;
    }

    private static bool[,] Dilate(bool[,] src, int h, int w, int radius)
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

    private static bool[,] Erode(bool[,] src, int h, int w, int radius)
    {
        var current = src;
        for (var iter = 0; iter < radius; iter++)
        {
            var next = new bool[h, w];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                if (!current[y, x]) continue;
                var left  = x == 0       || current[y, x - 1];
                var right = x == w - 1   || current[y, x + 1];
                var up    = y == 0       || current[y - 1, x];
                var down  = y == h - 1   || current[y + 1, x];
                if (left && right && up && down)
                    next[y, x] = true;
            }
            current = next;
        }
        return current;
    }

    private static List<List<(int x, int y)>> ConnectedComponents4(bool[,] mask, int h, int w)
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
