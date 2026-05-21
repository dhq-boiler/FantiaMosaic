namespace FantiaMosaic.Models;

public enum MosaicMode
{
    /// <summary>
    /// 各ブロックの平均色で塗りつぶすタイルモザイク。
    /// </summary>
    Pixelate,

    /// <summary>
    /// ガウシアンブラー。深いぼかし。
    /// </summary>
    GaussianBlur,

    /// <summary>
    /// ベタ塗り（指定色で完全に塗りつぶし）。
    /// </summary>
    SolidFill,

    /// <summary>
    /// タイルモザイク後、軽いブラーで段差を和らげる二段処理。
    /// 縮小しても階段状のエッジが原型の手掛かりにならないようにする。
    /// </summary>
    PixelateThenBlur,
}
