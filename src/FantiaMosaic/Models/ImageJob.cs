using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace FantiaMosaic.Models;

/// <summary>
/// 1枚の画像に対するモザイク処理ジョブ。
/// </summary>
public sealed class ImageJob
{
    public string SourcePath { get; init; } = string.Empty;
    public int OriginalWidth { get; set; }
    public int OriginalHeight { get; set; }
    public ObservableCollection<MosaicRegion> Regions { get; } = new();

    /// <summary>
    /// 出力ファイル名のサフィックス。
    /// </summary>
    public string OutputSuffix { get; set; } = "_mosaic";
}
