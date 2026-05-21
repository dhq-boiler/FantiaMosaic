using System;
using System.Collections.Generic;
using System.Linq;
using FantiaMosaic.Models;
using SkiaSharp;

namespace FantiaMosaic.Services;

/// <summary>
/// 既存モザイクが「傾いた矩形領域」に適用されている画像から、
/// その回転矩形を自動推定するディテクタ。
///
/// アルゴリズム:
///   1. グリッド状に小ブロック(probeTile px)に分割し、各ブロックの平均色と輝度分散を算出
///   2. 輝度分散が低い = 「平坦ブロック」 (タイル内部 or 均一背景)
///   3. 平坦ブロックの 4-連結成分を求める
///   4. 成分内のブロック平均色のばらつき(色多様性)が低いものは「均一背景」とみなして棄却
///      → 色多様性が高い成分のみがモザイク領域候補
///   5. 残った成分のブロック四隅頂点に対し 凸包 → Rotating Calipers で最小面積外接矩形を計算
/// </summary>
public sealed class RotatedMosaicDetector
{
    /// <summary>
    /// 任意角度の矩形領域。
    /// </summary>
    public sealed record OrientedRect(SKPoint Center, float Width, float Height, float AngleRad)
    {
        /// <summary>反時計回り順の頂点 4 個。</summary>
        public IReadOnlyList<SKPoint> Vertices()
        {
            var c = MathF.Cos(AngleRad);
            var s = MathF.Sin(AngleRad);
            var hw = Width / 2f;
            var hh = Height / 2f;

            SKPoint Rot(float x, float y) => new(x * c - y * s + Center.X, x * s + y * c + Center.Y);

            return new[]
            {
                Rot(-hw, -hh),
                Rot( hw, -hh),
                Rot( hw,  hh),
                Rot(-hw,  hh),
            };
        }

        public float AngleDegrees => AngleRad * 180f / MathF.PI;
    }

    public sealed class Options
    {
        /// <summary>ブロック1辺のpx。タイルサイズより小さく取ると検出力が上がる。</summary>
        public int ProbeTile { get; set; } = 4;
        /// <summary>これ以下の輝度分散を「平坦」と判定する。</summary>
        public double TileVarianceMax { get; set; } = 60.0;
        /// <summary>成分内のブロック平均輝度の標準偏差がこれ以上なら採用（均一背景の棄却基準）。</summary>
        public double ComponentColorDiversityMin { get; set; } = 18.0;
        /// <summary>成分ブロック数の最小値。</summary>
        public int MinBlockCount { get; set; } = 50;
        /// <summary>成分ブロック数の最大値（画面全体の何分の1まで許容するか）。0で無制限。</summary>
        public int MaxBlockCount { get; set; } = 0;
        /// <summary>1画像から返す上位検出数。</summary>
        public int TopK { get; set; } = 4;
        /// <summary>
        /// 平坦マップに対するモルフォロジカル closing 半径 (ブロック単位)。
        /// タイル境界の隙間を埋めるが、erodeも掛けるので背景には溢れ出さない。
        /// </summary>
        public int ClosingRadius { get; set; } = 2;
        /// <summary>
        /// 検出領域の最小縦横比 (max/min)。1 で無効化。
        /// </summary>
        public double MinAspectRatio { get; set; } = 1.0;
        /// <summary>
        /// 矩形の充填率の最小値。連結成分のブロック数 / 矩形面積比。
        /// 0で無効。葉っぱ等の細長く曲がった形状を棄却する目的。
        /// </summary>
        public double MinRectFillRatio { get; set; } = 0.35;
        /// <summary>
        /// 主要色の輝度からこの差以内の平坦ブロックは「均一面」として除外する。
        /// </summary>
        public double BackgroundExcludeRange { get; set; } = 20.0;
        /// <summary>
        /// 背景＋キャラの主要色など、除外する優勢色の数。
        /// イラスト系では 3〜4 (背景＋肌＋髪＋アクセサリ程度) が無難。
        /// </summary>
        public int DominantColorsToExclude { get; set; } = 4;
        /// <summary>
        /// 検出された小領域同士をこの距離(px)以下で接続して1クラスタにする。
        /// 大きい値だと別物のモザイク同士まで合体し誤検出になるため、控えめに設定する。
        /// 0で合体無効（個別矩形をそのまま返す）。
        /// </summary>
        public double ClusterDistance { get; set; } = 16.0;
    }

    private static List<float> EstimateDominantFlatLumas(float[,] avgL, bool[,] isFlat, int rows, int cols, int topN)
    {
        var hist = new int[256];
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < cols; x++)
        {
            if (!isFlat[y, x]) continue;
            var bin = Math.Clamp((int)avgL[y, x], 0, 255);
            hist[bin]++;
        }
        // Non-maximum suppression: 半径 W 内に他の極大が無いビンだけをピーク採用
        const int suppressRadius = 8;
        var peaks = new List<(int bin, int count)>();
        for (var i = 0; i < 256; i++)
        {
            if (hist[i] == 0) continue;
            var isPeak = true;
            for (var k = 1; k <= suppressRadius; k++)
            {
                if (i - k >= 0 && hist[i - k] > hist[i]) { isPeak = false; break; }
                if (i + k < 256 && hist[i + k] > hist[i]) { isPeak = false; break; }
            }
            if (isPeak) peaks.Add((i, hist[i]));
        }
        return peaks
            .OrderByDescending(p => p.count)
            .Take(topN)
            .Select(p => (float)p.bin)
            .ToList();
    }

    /// <summary>
    /// 検出を実行し、面積が大きい順に最大 <see cref="Options.TopK"/> 個の回転矩形を返す。
    /// </summary>
    public IReadOnlyList<OrientedRect> Detect(SKBitmap bitmap, Options? options = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var opt = options ?? new Options();

        var w = bitmap.Width;
        var h = bitmap.Height;
        var T = Math.Max(2, opt.ProbeTile);
        var cols = w / T;
        var rows = h / T;
        if (cols < 4 || rows < 4) return Array.Empty<OrientedRect>();

        // ---------- ピクセルを一括取得 ----------
        var pixels = bitmap.Pixels; // SKColor[w*h]

        // ---------- ブロック平均と平坦判定 ----------
        var avgL = new float[rows, cols];
        var isFlat = new bool[rows, cols];

        for (var by = 0; by < rows; by++)
        for (var bx = 0; bx < cols; bx++)
        {
            double sumL = 0, sumLsq = 0;
            var n = 0;
            var x0 = bx * T;
            var y0 = by * T;
            var xMax = Math.Min(x0 + T, w);
            var yMax = Math.Min(y0 + T, h);
            for (var y = y0; y < yMax; y++)
            for (var x = x0; x < xMax; x++)
            {
                var c = pixels[y * w + x];
                var L = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                sumL += L;
                sumLsq += L * L;
                n++;
            }
            if (n == 0) continue;
            var mean = sumL / n;
            var variance = sumLsq / n - mean * mean;
            avgL[by, bx] = (float)mean;
            isFlat[by, bx] = variance < opt.TileVarianceMax;
        }

        // ---------- 主要均一色を推定して除外 ----------
        // 平坦ブロックの輝度ヒストグラムから上位 N 個のピーク(=背景・キャラの主要色)
        // を求め、それに近い輝度のブロックを除外する。
        // これでモザイク領域が背景・キャラの単色面と連結成分でくっつかなくなる。
        var dominantLumas = EstimateDominantFlatLumas(avgL, isFlat, rows, cols, opt.DominantColorsToExclude);
        var isCandidate = new bool[rows, cols];
        for (var by = 0; by < rows; by++)
        for (var bx = 0; bx < cols; bx++)
        {
            if (!isFlat[by, bx]) continue;
            var L = avgL[by, bx];
            var isDominant = false;
            foreach (var d in dominantLumas)
            {
                if (Math.Abs(L - d) <= opt.BackgroundExcludeRange) { isDominant = true; break; }
            }
            if (isDominant) continue;
            isCandidate[by, bx] = true;
        }

        // ---------- モルフォロジカル closing ----------
        // 候補マップに対してのみ closing をかける。背景は既に除外済みなので
        // 隣接モザイクタイル同士の連結だけを助ける。
        var flatMap = isCandidate;
        if (opt.ClosingRadius > 0)
        {
            var dilated = Dilate(isCandidate, rows, cols, opt.ClosingRadius);
            flatMap = Erode(dilated, rows, cols, opt.ClosingRadius);
        }

        // ---------- 連結成分 (4近傍) ----------
        var labels = new int[rows, cols];
        var components = new List<List<(int x, int y)>>();
        var nextLabel = 0;

        for (var by = 0; by < rows; by++)
        for (var bx = 0; bx < cols; bx++)
        {
            if (!flatMap[by, bx] || labels[by, bx] != 0) continue;
            nextLabel++;
            var comp = new List<(int x, int y)>();
            var stack = new Stack<(int x, int y)>();
            stack.Push((bx, by));
            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Pop();
                if ((uint)cx >= (uint)cols || (uint)cy >= (uint)rows) continue;
                if (!flatMap[cy, cx] || labels[cy, cx] != 0) continue;
                labels[cy, cx] = nextLabel;
                comp.Add((cx, cy));
                stack.Push((cx + 1, cy));
                stack.Push((cx - 1, cy));
                stack.Push((cx, cy + 1));
                stack.Push((cx, cy - 1));
            }
            components.Add(comp);
        }

        // ---------- フィルタリング & 回転矩形抽出 ----------
        var diags = new List<DetectionDiagnostic>();
        var maxBlocks = opt.MaxBlockCount > 0 ? opt.MaxBlockCount : (rows * cols / 3);
        var results = new List<OrientedRect>();

        foreach (var comp in components)
        {
            var diag = new DetectionDiagnostic { BlockCount = comp.Count };

            if (comp.Count < opt.MinBlockCount) { diag.RejectedReason = "size<min"; diags.Add(diag); continue; }
            if (comp.Count > maxBlocks)         { diag.RejectedReason = "size>max"; diags.Add(diag); continue; }

            // 色多様性 (成分全体のブロック平均輝度の標準偏差)
            double sum = 0, sumSq = 0;
            foreach (var (bx, by) in comp)
            {
                var v = avgL[by, bx];
                sum += v;
                sumSq += v * v;
            }
            var mean = sum / comp.Count;
            var variance = sumSq / comp.Count - mean * mean;
            var stddev = Math.Sqrt(Math.Max(0, variance));
            diag.ColorStdDev = stddev;
            if (stddev < opt.ComponentColorDiversityMin)
            {
                diag.RejectedReason = "diversity<min";
                diags.Add(diag);
                continue;
            }

            // 4隅をピクセル座標で展開 → 最小面積回転矩形
            var pts = new List<SKPoint>(comp.Count * 4);
            foreach (var (bx, by) in comp)
            {
                var x0 = bx * T; var y0 = by * T;
                var x1 = x0 + T; var y1 = y0 + T;
                pts.Add(new SKPoint(x0, y0));
                pts.Add(new SKPoint(x1, y0));
                pts.Add(new SKPoint(x0, y1));
                pts.Add(new SKPoint(x1, y1));
            }
            var rect = MinAreaRect(pts);
            diag.Width = rect.Width;
            diag.Height = rect.Height;
            diag.AngleDeg = rect.AngleDegrees;
            if (rect.Width < T || rect.Height < T) { diag.RejectedReason = "rect<probeTile"; diags.Add(diag); continue; }

            // 充填率
            var rectArea = rect.Width * rect.Height;
            var fillRatio = comp.Count * (double)(T * T) / Math.Max(1, rectArea);
            diag.FillRatio = fillRatio;
            if (fillRatio < opt.MinRectFillRatio)
            {
                diag.RejectedReason = "fill<min";
                diags.Add(diag);
                continue;
            }

            // 縦横比フィルタ
            var longSide = Math.Max(rect.Width, rect.Height);
            var shortSide = Math.Max(1, Math.Min(rect.Width, rect.Height));
            if (longSide / shortSide < opt.MinAspectRatio)
            {
                diag.RejectedReason = "aspect<min";
                diags.Add(diag);
                continue;
            }

            diag.Accepted = true;
            diags.Add(diag);
            results.Add(rect);
        }

        LastDiagnostics = diags;

        // 空間的に近い小領域は1つのクラスタに統合して、最終的に大きな回転矩形で囲む。
        // モザイク領域は内部に「白マージン」など除外色を含むため、検出が断片化する傾向がある。
        // これらを1つの回転矩形に合体させることで、ユーザー視点での「札全体」を得る。
        var clustered = ClusterAndMerge(results, opt.ClusterDistance);

        return clustered.OrderByDescending(r => r.Width * r.Height).Take(opt.TopK).ToList();
    }

    /// <summary>
    /// 矩形が実際に近接する場合のみ1クラスタとしてマージする。
    /// 距離は「矩形間の最短ピクセル距離」を使用 (中心距離ではない)。
    /// </summary>
    private static List<OrientedRect> ClusterAndMerge(List<OrientedRect> rects, double maxDist)
    {
        if (rects.Count <= 1 || maxDist <= 0) return rects;

        // 各矩形の頂点をあらかじめ計算
        var verts = rects.Select(r => r.Vertices().ToArray()).ToArray();

        var parent = Enumerable.Range(0, rects.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
        void Union(int a, int b)
        {
            var ra = Find(a); var rb = Find(b);
            if (ra != rb) parent[ra] = rb;
        }

        for (var i = 0; i < rects.Count; i++)
        for (var j = i + 1; j < rects.Count; j++)
        {
            if (RectsMinDistance(verts[i], verts[j]) <= maxDist)
                Union(i, j);
        }

        var groups = new Dictionary<int, List<int>>();
        for (var i = 0; i < rects.Count; i++)
        {
            var r = Find(i);
            if (!groups.TryGetValue(r, out var lst)) groups[r] = lst = new();
            lst.Add(i);
        }

        var merged = new List<OrientedRect>();
        foreach (var g in groups.Values)
        {
            if (g.Count == 1) { merged.Add(rects[g[0]]); continue; }
            var pts = new List<SKPoint>();
            foreach (var idx in g) pts.AddRange(verts[idx]);
            merged.Add(MinAreaRect(pts));
        }
        return merged;
    }

    /// <summary>2つの矩形(頂点)の最短点間距離をサンプリング近似で返す。</summary>
    private static double RectsMinDistance(SKPoint[] a, SKPoint[] b)
    {
        // 矩形が重なっていれば 0
        if (PolygonsIntersect(a, b)) return 0;
        // 各頂点-辺の距離を試して最小値を取る
        var min = double.MaxValue;
        foreach (var p in a)
            foreach (var (s, e) in EdgePairs(b))
                min = Math.Min(min, PointToSegmentDist(p, s, e));
        foreach (var p in b)
            foreach (var (s, e) in EdgePairs(a))
                min = Math.Min(min, PointToSegmentDist(p, s, e));
        return min;
    }

    private static IEnumerable<(SKPoint s, SKPoint e)> EdgePairs(SKPoint[] poly)
    {
        for (var i = 0; i < poly.Length; i++)
            yield return (poly[i], poly[(i + 1) % poly.Length]);
    }

    private static double PointToSegmentDist(SKPoint p, SKPoint a, SKPoint b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-9) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
        t = Math.Clamp(t, 0, 1);
        var cx = a.X + t * dx;
        var cy = a.Y + t * dy;
        return Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
    }

    /// <summary>SAT による凸多角形の重なり判定。</summary>
    private static bool PolygonsIntersect(SKPoint[] a, SKPoint[] b)
    {
        return SatNoSepAxis(a, a, b) && SatNoSepAxis(b, a, b);
    }

    private static bool SatNoSepAxis(SKPoint[] poly, SKPoint[] a, SKPoint[] b)
    {
        for (var i = 0; i < poly.Length; i++)
        {
            var p1 = poly[i];
            var p2 = poly[(i + 1) % poly.Length];
            float nx = -(p2.Y - p1.Y), ny = (p2.X - p1.X);
            ProjectPolygon(a, nx, ny, out var minA, out var maxA);
            ProjectPolygon(b, nx, ny, out var minB, out var maxB);
            if (maxA < minB || maxB < minA) return false;
        }
        return true;
    }

    private static void ProjectPolygon(SKPoint[] poly, float nx, float ny, out float min, out float max)
    {
        min = float.MaxValue; max = float.MinValue;
        foreach (var p in poly)
        {
            var d = p.X * nx + p.Y * ny;
            if (d < min) min = d;
            if (d > max) max = d;
        }
    }

    public sealed class DetectionDiagnostic
    {
        public int BlockCount;
        public double ColorStdDev;
        public float Width;
        public float Height;
        public float AngleDeg;
        public double FillRatio;
        public bool Accepted;
        public string RejectedReason = string.Empty;
    }

    /// <summary>直前の Detect 呼び出しで生成された成分ごとの診断情報。</summary>
    public IReadOnlyList<DetectionDiagnostic> LastDiagnostics { get; private set; } = Array.Empty<DetectionDiagnostic>();

    /// <summary>
    /// ブロック単位の二値マップに対する 4近傍膨張。
    /// </summary>
    private static bool[,] Dilate(bool[,] src, int rows, int cols, int radius)
    {
        var current = src;
        for (var iter = 0; iter < radius; iter++)
        {
            var next = new bool[rows, cols];
            for (var y = 0; y < rows; y++)
            for (var x = 0; x < cols; x++)
            {
                if (current[y, x])
                {
                    next[y, x] = true;
                }
                else
                {
                    if ((x > 0 && current[y, x - 1])
                     || (x < cols - 1 && current[y, x + 1])
                     || (y > 0 && current[y - 1, x])
                     || (y < rows - 1 && current[y + 1, x]))
                        next[y, x] = true;
                }
            }
            current = next;
        }
        return current;
    }

    /// <summary>
    /// ブロック単位の二値マップに対する 4近傍収縮。
    /// </summary>
    private static bool[,] Erode(bool[,] src, int rows, int cols, int radius)
    {
        var current = src;
        for (var iter = 0; iter < radius; iter++)
        {
            var next = new bool[rows, cols];
            for (var y = 0; y < rows; y++)
            for (var x = 0; x < cols; x++)
            {
                if (!current[y, x]) continue;
                var left   = x > 0           && current[y, x - 1];
                var right  = x < cols - 1    && current[y, x + 1];
                var up     = y > 0           && current[y - 1, x];
                var down   = y < rows - 1    && current[y + 1, x];
                if (left && right && up && down)
                    next[y, x] = true;
            }
            current = next;
        }
        return current;
    }

    /// <summary>
    /// 検出結果をそのまま <see cref="MosaicRegion"/> ポリゴンとして返す簡便ラッパ。
    /// </summary>
    public IReadOnlyList<MosaicRegion> DetectAsRegions(SKBitmap bitmap, Options? options = null)
    {
        var rects = Detect(bitmap, options);
        var list = new List<MosaicRegion>(rects.Count);
        foreach (var r in rects)
            list.Add(MosaicRegion.FromPolygon(r.Vertices().ToList()));
        return list;
    }

    // ====================================================================
    // 凸包と Rotating Calipers
    // ====================================================================

    /// <summary>外部からも使えるよう公開した最小面積回転矩形計算。</summary>
    public static OrientedRect MinAreaRectPublic(IList<SKPoint> points) => MinAreaRect(points);

    /// <summary>
    /// 重み付き主成分分析。weights が null なら均等重み。
    /// 高信頼度ピクセルに引きずられすぎないよう、扁平な形状でも安定して長軸を取れる。
    /// </summary>
    public static OrientedRect WeightedPcaRect(IList<SKPoint> points, IList<float>? weights)
    {
        if (points.Count < 3) return MinAreaRect(points);
        if (weights != null && weights.Count != points.Count)
            throw new ArgumentException("weights length must match points");

        double totalW = 0;
        double mx = 0, my = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var w = weights?[i] ?? 1f;
            mx += points[i].X * w;
            my += points[i].Y * w;
            totalW += w;
        }
        if (totalW < 1e-6) return MinAreaRect(points);
        mx /= totalW;
        my /= totalW;

        double cxx = 0, cyy = 0, cxy = 0;
        for (var i = 0; i < points.Count; i++)
        {
            var w = weights?[i] ?? 1f;
            var dx = points[i].X - mx;
            var dy = points[i].Y - my;
            cxx += w * dx * dx;
            cyy += w * dy * dy;
            cxy += w * dx * dy;
        }
        cxx /= totalW;
        cyy /= totalW;
        cxy /= totalW;

        var theta = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);

        double minU = double.MaxValue, maxU = double.MinValue;
        double minV = double.MaxValue, maxV = double.MinValue;
        foreach (var p in points)
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
        var width = (float)(maxU - minU);
        var height = (float)(maxV - minV);
        var cu = (minU + maxU) / 2;
        var cv = (minV + maxV) / 2;
        var cxOut = (float)(mx + cu * cos - cv * sin);
        var cyOut = (float)(my + cu * sin + cv * cos);
        return new OrientedRect(new SKPoint(cxOut, cyOut), width, height, (float)theta);
    }

    /// <summary>
    /// 点群の主成分分析 (PCA) で主軸方向を求め、その軸並びの外接矩形を返す。
    /// </summary>
    public static OrientedRect PcaRect(IList<SKPoint> points)
    {
        if (points.Count < 3) return MinAreaRect(points);

        // 平均
        double mx = 0, my = 0;
        foreach (var p in points) { mx += p.X; my += p.Y; }
        mx /= points.Count;
        my /= points.Count;

        // 共分散行列 (対称 2x2)
        double cxx = 0, cyy = 0, cxy = 0;
        foreach (var p in points)
        {
            var dx = p.X - mx;
            var dy = p.Y - my;
            cxx += dx * dx;
            cyy += dy * dy;
            cxy += dx * dy;
        }
        cxx /= points.Count;
        cyy /= points.Count;
        cxy /= points.Count;

        // 最大固有値に対応する主軸方向 θ
        // 2x2 対称行列の固有ベクトル角度: θ = 0.5 * atan2(2*Cxy, Cxx - Cyy)
        var theta = 0.5 * Math.Atan2(2 * cxy, cxx - cyy);
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);

        // 主軸座標系に変換し AABB を取る
        double minU = double.MaxValue, maxU = double.MinValue;
        double minV = double.MaxValue, maxV = double.MinValue;
        foreach (var p in points)
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
        var width = (float)(maxU - minU);
        var height = (float)(maxV - minV);
        var cu = (minU + maxU) / 2;
        var cv = (minV + maxV) / 2;
        var cxOut = (float)(mx + cu * cos - cv * sin);
        var cyOut = (float)(my + cu * sin + cv * cos);
        return new OrientedRect(new SKPoint(cxOut, cyOut), width, height, (float)theta);
    }

    private static OrientedRect MinAreaRect(IList<SKPoint> points)
    {
        var hull = ConvexHull(points);
        if (hull.Count < 3)
        {
            float l = float.MaxValue, t = float.MaxValue, r = float.MinValue, b = float.MinValue;
            foreach (var p in points)
            {
                if (p.X < l) l = p.X;
                if (p.Y < t) t = p.Y;
                if (p.X > r) r = p.X;
                if (p.Y > b) b = p.Y;
            }
            return new OrientedRect(new SKPoint((l + r) / 2, (t + b) / 2), r - l, b - t, 0);
        }

        double minArea = double.MaxValue;
        OrientedRect best = new(SKPoint.Empty, 0, 0, 0);

        for (var i = 0; i < hull.Count; i++)
        {
            var p1 = hull[i];
            var p2 = hull[(i + 1) % hull.Count];
            var ex = p2.X - p1.X;
            var ey = p2.Y - p1.Y;
            var len = MathF.Sqrt(ex * ex + ey * ey);
            if (len < 1e-6f) continue;

            var ux = ex / len;
            var uy = ey / len;
            // 垂直軸
            var vx = -uy;
            var vy = ux;

            float minU = float.MaxValue, maxU = float.MinValue;
            float minV = float.MaxValue, maxV = float.MinValue;
            foreach (var p in hull)
            {
                var u = p.X * ux + p.Y * uy;
                var v = p.X * vx + p.Y * vy;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
            var width = maxU - minU;
            var height = maxV - minV;
            var area = (double)width * height;
            if (area < minArea)
            {
                minArea = area;
                var cu = (minU + maxU) / 2f;
                var cv = (minV + maxV) / 2f;
                var cx = cu * ux + cv * vx;
                var cy = cu * uy + cv * vy;
                var angle = MathF.Atan2(uy, ux);
                best = new OrientedRect(new SKPoint(cx, cy), width, height, angle);
            }
        }
        return best;
    }

    /// <summary>
    /// Andrew's monotone chain による凸包。反時計回り。
    /// </summary>
    private static List<SKPoint> ConvexHull(IList<SKPoint> input)
    {
        if (input.Count < 3) return input.ToList();

        // ソート + 重複除去
        var sorted = input.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        var deduped = new List<SKPoint>(sorted.Count);
        foreach (var p in sorted)
            if (deduped.Count == 0 || deduped[^1] != p) deduped.Add(p);
        if (deduped.Count < 3) return deduped;

        var hull = new List<SKPoint>();

        // 下側
        foreach (var p in deduped)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        // 上側
        var lowerCount = hull.Count + 1;
        for (var i = deduped.Count - 2; i >= 0; i--)
        {
            var p = deduped[i];
            while (hull.Count >= lowerCount && Cross(hull[^2], hull[^1], p) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        if (hull.Count > 0) hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static double Cross(SKPoint O, SKPoint A, SKPoint B)
        => (double)(A.X - O.X) * (B.Y - O.Y) - (double)(A.Y - O.Y) * (B.X - O.X);
}
