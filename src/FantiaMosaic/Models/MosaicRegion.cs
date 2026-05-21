using System;
using System.Collections.Generic;
using SkiaSharp;

namespace FantiaMosaic.Models;

/// <summary>
/// モザイクを適用する領域の形状種別。
/// </summary>
public enum RegionShape
{
    Rectangle,
    Ellipse,
    Polygon,
    /// <summary>中心・サイズ・角度で定義される回転矩形。UI でハンドル編集できる。</summary>
    OrientedRectangle,
}

/// <summary>
/// 画像座標系（原寸ピクセル）でのモザイク領域定義。
/// </summary>
public sealed class MosaicRegion
{
    public RegionShape Shape { get; set; } = RegionShape.Rectangle;

    /// <summary>
    /// 矩形/楕円: 境界矩形。ポリゴン: 境界ボックス（描画ヒント）。
    /// OrientedRectangle: 軸並行AABB（描画ヒントとしてのみ使用）。
    /// </summary>
    public SKRect Bounds { get; set; }

    /// <summary>
    /// ポリゴン頂点 (画像座標)。Polygon 以外では空。
    /// </summary>
    public IReadOnlyList<SKPoint> Points { get; set; } = Array.Empty<SKPoint>();

    // ---- OrientedRectangle 用 ----
    public SKPoint Center { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    /// <summary>ラジアン。0 で軸並行、正は時計回り。</summary>
    public float AngleRad { get; set; }

    /// <summary>
    /// この領域だけで個別設定を上書きしたい場合。null なら全体設定を使う。
    /// </summary>
    public MosaicSettings? Override { get; set; }

    /// <summary>
    /// SolidFill モード時の領域別塗りつぶし色。null なら全体設定 SolidColor を使う。
    /// 領域作成時にユーザーが選んでた色をスナップショットしておくことで、
    /// 後で全体の色を変えても既存領域には影響しないようにする。
    /// </summary>
    public SKColor? FillColor { get; set; }

    public static MosaicRegion FromRect(SKRect rect) => new()
    {
        Shape = RegionShape.Rectangle,
        Bounds = rect,
    };

    public static MosaicRegion FromEllipse(SKRect rect) => new()
    {
        Shape = RegionShape.Ellipse,
        Bounds = rect,
    };

    public static MosaicRegion FromPolygon(IReadOnlyList<SKPoint> points)
    {
        if (points.Count < 3)
            throw new ArgumentException("ポリゴンには3点以上必要っす", nameof(points));

        float minX = points[0].X, minY = points[0].Y, maxX = minX, maxY = minY;
        for (var i = 1; i < points.Count; i++)
        {
            var p = points[i];
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return new MosaicRegion
        {
            Shape = RegionShape.Polygon,
            Bounds = new SKRect(minX, minY, maxX, maxY),
            Points = points,
        };
    }

    /// <summary>
    /// 中心・幅・高さ・角度で回転矩形を作る。UI でハンドル編集できる形式。
    /// </summary>
    public static MosaicRegion FromOrientedRect(SKPoint center, float width, float height, float angleRad)
    {
        var r = new MosaicRegion
        {
            Shape = RegionShape.OrientedRectangle,
            Center = center,
            Width = width,
            Height = height,
            AngleRad = angleRad,
        };
        r.RecomputeFromOrientedRect();
        return r;
    }

    /// <summary>
    /// OrientedRectangle の Center/Width/Height/AngleRad から Bounds と Points を再計算する。
    /// UI でハンドル操作した直後に呼ぶ。
    /// </summary>
    public void RecomputeFromOrientedRect()
    {
        var c = (float)Math.Cos(AngleRad);
        var s = (float)Math.Sin(AngleRad);
        var hw = Width / 2f;
        var hh = Height / 2f;
        SKPoint Rot(float x, float y) => new(x * c - y * s + Center.X, x * s + y * c + Center.Y);
        var v = new[] { Rot(-hw, -hh), Rot(hw, -hh), Rot(hw, hh), Rot(-hw, hh) };
        Points = v;

        float minX = v[0].X, minY = v[0].Y, maxX = v[0].X, maxY = v[0].Y;
        for (var i = 1; i < 4; i++)
        {
            if (v[i].X < minX) minX = v[i].X;
            if (v[i].Y < minY) minY = v[i].Y;
            if (v[i].X > maxX) maxX = v[i].X;
            if (v[i].Y > maxY) maxY = v[i].Y;
        }
        Bounds = new SKRect(minX, minY, maxX, maxY);
    }
}
