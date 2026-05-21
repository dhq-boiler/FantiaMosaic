using System.Collections.Generic;
using FantiaMosaic.Models;
using SkiaSharp;

namespace FantiaMosaic.Services;

public interface IMosaicEngine
{
    /// <summary>
    /// 指定領域にモザイクを適用して新規ビットマップを返す。元画像は変更しない。
    /// </summary>
    SKBitmap Apply(SKBitmap source, IReadOnlyList<MosaicRegion> regions, MosaicSettings settings);

    /// <summary>
    /// ガイドライン適合チェック。縮小しても原型が判別できない強度になっているかを判定。
    /// </summary>
    GuidelineCheckResult CheckCompliance(int imageWidth, int imageHeight, MosaicSettings settings);
}

public sealed record GuidelineCheckResult(
    bool IsCompliant,
    int EffectiveStrengthPx,
    int ShortSidePx,
    string Detail);
