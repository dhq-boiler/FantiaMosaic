using System;
using System.Collections.Generic;
using FantiaMosaic.Models;
using SkiaSharp;

namespace FantiaMosaic.Services;

/// <summary>
/// 既存の弱モザイク領域を「局所分散が低く・規則的なブロック構造を持つ」ことから推定する。
///
/// このツールの想定ユースケースは『既にFantiaにアップしてしまった画像を、改定後も大丈夫な強度に直す』こと。
/// ユーザーが領域を覚えていない場合に備え、ヒント用ヒートマップを作る役目。最終判断は人間。
/// </summary>
public sealed class ExistingMosaicDetector
{
    /// <summary>
    /// ブロック単位の局所分散と勾配規則性から候補矩形を抽出する。
    /// </summary>
    /// <param name="bitmap">対象画像</param>
    /// <param name="probeTile">解析ブロックの一辺 (px)</param>
    /// <param name="varianceThreshold">この値以下を「平坦」と判定 (0-65025)</param>
    /// <returns>連結成分の境界矩形リスト</returns>
    public IReadOnlyList<SKRectI> Detect(SKBitmap bitmap, int probeTile = 16, double varianceThreshold = 120.0)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (probeTile < 4) probeTile = 4;

        var cols = bitmap.Width / probeTile;
        var rows = bitmap.Height / probeTile;
        if (cols <= 0 || rows <= 0) return Array.Empty<SKRectI>();

        var flat = new bool[rows, cols];

        for (var by = 0; by < rows; by++)
        for (var bx = 0; bx < cols; bx++)
        {
            var variance = ComputeBlockLuminanceVariance(bitmap, bx * probeTile, by * probeTile, probeTile);
            if (variance <= varianceThreshold)
                flat[by, bx] = true;
        }

        // 4近傍で連結成分ラベリングしてヒート矩形にする
        var labels = new int[rows, cols];
        var rects = new List<SKRectI>();
        var nextLabel = 0;

        for (var by = 0; by < rows; by++)
        for (var bx = 0; bx < cols; bx++)
        {
            if (!flat[by, bx] || labels[by, bx] != 0) continue;

            nextLabel++;
            int minX = bx, minY = by, maxX = bx, maxY = by;
            var stack = new Stack<(int x, int y)>();
            stack.Push((bx, by));

            while (stack.Count > 0)
            {
                var (x, y) = stack.Pop();
                if (x < 0 || y < 0 || x >= cols || y >= rows) continue;
                if (!flat[y, x] || labels[y, x] != 0) continue;

                labels[y, x] = nextLabel;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;

                stack.Push((x + 1, y));
                stack.Push((x - 1, y));
                stack.Push((x, y + 1));
                stack.Push((x, y - 1));
            }

            // 小さすぎる候補は捨てる（ノイズや空・服の単色領域は誤検出になりやすい）
            var w = maxX - minX + 1;
            var h = maxY - minY + 1;
            if (w * h < 6) continue;

            rects.Add(new SKRectI(
                minX * probeTile,
                minY * probeTile,
                (maxX + 1) * probeTile,
                (maxY + 1) * probeTile));
        }

        return rects;
    }

    /// <summary>
    /// 検出結果を MosaicRegion 列に変換するヘルパ。
    /// </summary>
    public IReadOnlyList<MosaicRegion> DetectAsRegions(SKBitmap bitmap)
    {
        var rects = Detect(bitmap);
        var list = new List<MosaicRegion>(rects.Count);
        foreach (var r in rects)
            list.Add(MosaicRegion.FromRect(new SKRect(r.Left, r.Top, r.Right, r.Bottom)));
        return list;
    }

    private static double ComputeBlockLuminanceVariance(SKBitmap bmp, int x0, int y0, int tile)
    {
        var n = 0;
        double sum = 0, sumSq = 0;
        var xMax = Math.Min(x0 + tile, bmp.Width);
        var yMax = Math.Min(y0 + tile, bmp.Height);

        for (var y = y0; y < yMax; y++)
        for (var x = x0; x < xMax; x++)
        {
            var c = bmp.GetPixel(x, y);
            // BT.601 輝度
            var l = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
            sum += l;
            sumSq += l * l;
            n++;
        }

        if (n == 0) return 0;
        var mean = sum / n;
        return sumSq / n - mean * mean;
    }
}
