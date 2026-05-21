using SkiaSharp;

namespace FantiaMosaic.Models;

/// <summary>
/// Fantia 2026-05-25 改定ガイドラインに従ったモザイク強度設定。
///
/// 重要な前提:
///   公式ガイドは「最小4px などの絶対値基準では縮小時に原型が視認できる」と明示。
///   そのため本ツールでは強度を画像短辺に対する <see cref="RelativeStrength"/>（割合）で決め、
///   絶対値は <see cref="MinimumStrengthPx"/> でしか効かせない。
/// </summary>
public sealed class MosaicSettings
{
    public MosaicMode Mode { get; set; } = MosaicMode.PixelateThenBlur;

    /// <summary>
    /// 画像の短辺に対するモザイク基本強度の割合 (0.0 - 1.0)。
    /// 例: 0.03 なら短辺の 3% をタイルサイズ / ブラー半径の目安とする。
    /// </summary>
    public double RelativeStrength { get; set; } = 0.03;

    /// <summary>
    /// 縮小されても破綻しないための絶対下限 (px)。
    /// 小さな画像で割合計算が極小になっても、必ずこの値以上を採用する。
    /// </summary>
    public int MinimumStrengthPx { get; set; } = 16;

    /// <summary>
    /// ベタ塗りモード時の色。
    /// </summary>
    public SKColor SolidColor { get; set; } = SKColors.Black;

    /// <summary>
    /// <see cref="MosaicMode.PixelateThenBlur"/> の後段ブラーの強度割合。
    /// タイルサイズに対するブラー半径の比率 (0.0 - 1.0)。0 で後段ブラー無効。
    /// </summary>
    public double PostBlurRatio { get; set; } = 0.25;

    /// <summary>
    /// 出力時にあわせて生成するサムネイル長辺 (px)。0 で生成しない。
    /// </summary>
    public int ThumbnailLongSide { get; set; } = 0;

    /// <summary>
    /// 動画/高解像度向け強モードプリセット。
    /// ガイドの「静止画より一段強く深く」要件に対応。
    /// </summary>
    public static MosaicSettings StrongPreset() => new()
    {
        Mode = MosaicMode.PixelateThenBlur,
        RelativeStrength = 0.05,
        MinimumStrengthPx = 24,
        PostBlurRatio = 0.4,
    };

    /// <summary>
    /// 通常の静止画向け推奨プリセット。
    /// </summary>
    public static MosaicSettings DefaultPreset() => new()
    {
        Mode = MosaicMode.PixelateThenBlur,
        RelativeStrength = 0.03,
        MinimumStrengthPx = 16,
        PostBlurRatio = 0.25,
    };

    /// <summary>
    /// 与えられた画像サイズに対する実効タイルサイズ / ブラー半径 (px) を計算。
    /// </summary>
    public int ResolveStrengthPx(int imageWidth, int imageHeight)
    {
        var shortSide = Math.Min(imageWidth, imageHeight);
        var byRatio = (int)Math.Round(shortSide * RelativeStrength);
        return Math.Max(byRatio, MinimumStrengthPx);
    }
}
