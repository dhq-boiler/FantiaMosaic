using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FantiaMosaic.Models;
using SkiaSharp;

namespace FantiaMosaic.Services;

/// <summary>
/// 既存 UI で描かれた回転矩形を YOLO-OBB 形式の学習データセットとしてエクスポートする。
///
/// 出力構造:
///   output_dir/
///   ├── images/        ← 画像ファイル (PNG)
///   ├── labels/        ← YOLO-OBB ラベルファイル (.txt)
///   ├── data.yaml      ← Ultralytics 用データセット記述
///   └── README.md      ← 使い方メモ
///
/// ラベル形式 (1行 = 1領域):
///   class_id x1 y1 x2 y2 x3 y3 x4 y4
///   座標は画像幅・高さで normalize された 0-1 値。
///   class_id は単一クラス "occlusion" を 0 とする。
/// </summary>
public sealed class DatasetExporter
{
    public sealed record ExportResult(int ImagesExported, int LabelsExported, string OutputDir);

    public sealed record ExportProgress(int Done, int Total, string CurrentFile);

    private readonly IImageIo _io;

    public DatasetExporter(IImageIo io)
    {
        _io = io;
    }

    public async Task<ExportResult> ExportAsync(
        IReadOnlyList<ImageJob> jobs,
        string outputDir,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
            throw new ArgumentException("出力先が空っす", nameof(outputDir));

        var imagesDir = Path.Combine(outputDir, "images");
        var labelsDir = Path.Combine(outputDir, "labels");
        Directory.CreateDirectory(imagesDir);
        Directory.CreateDirectory(labelsDir);

        var withGt = jobs.Where(j => j.Regions.Count > 0).ToList();
        if (withGt.Count == 0)
            throw new InvalidOperationException("GT (正解矩形) を描いた画像がひとつもないっす。");

        var done = 0;
        var imagesExported = 0;
        var labelsExported = 0;

        await Task.Run(() =>
        {
            for (var i = 0; i < withGt.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = withGt[i];
                var stem = $"{i:D4}_{Path.GetFileNameWithoutExtension(job.SourcePath)}";
                var imgPath = Path.Combine(imagesDir, stem + ".png");
                var labelPath = Path.Combine(labelsDir, stem + ".txt");

                using var bmp = _io.Load(job.SourcePath);
                _io.Save(bmp, imgPath, quality: 100);
                imagesExported++;

                var sb = new StringBuilder();
                foreach (var region in job.Regions)
                {
                    var verts = GetVertices(region);
                    if (verts.Count != 4) continue; // 矩形系のみ採用
                    var nx = (double)bmp.Width;
                    var ny = (double)bmp.Height;
                    var inv = CultureInfo.InvariantCulture;
                    sb.Append("0"); // class_id
                    foreach (var p in verts)
                    {
                        sb.Append(' ');
                        sb.Append((p.X / nx).ToString("F6", inv));
                        sb.Append(' ');
                        sb.Append((p.Y / ny).ToString("F6", inv));
                    }
                    sb.AppendLine();
                    labelsExported++;
                }
                File.WriteAllText(labelPath, sb.ToString());

                done++;
                progress?.Report(new ExportProgress(done, withGt.Count, Path.GetFileName(job.SourcePath)));
            }
        }, cancellationToken);

        WriteDataYaml(outputDir);
        WriteReadme(outputDir);

        return new ExportResult(imagesExported, labelsExported, outputDir);
    }

    private static IReadOnlyList<SKPoint> GetVertices(MosaicRegion region)
    {
        return region.Shape switch
        {
            // 軸並行矩形は 4 隅に展開
            RegionShape.Rectangle => new[]
            {
                new SKPoint(region.Bounds.Left, region.Bounds.Top),
                new SKPoint(region.Bounds.Right, region.Bounds.Top),
                new SKPoint(region.Bounds.Right, region.Bounds.Bottom),
                new SKPoint(region.Bounds.Left, region.Bounds.Bottom),
            },
            // 楕円は近似的に外接矩形を採用 (学習データとしては使わない方が無難だが含めておく)
            RegionShape.Ellipse => new[]
            {
                new SKPoint(region.Bounds.Left, region.Bounds.Top),
                new SKPoint(region.Bounds.Right, region.Bounds.Top),
                new SKPoint(region.Bounds.Right, region.Bounds.Bottom),
                new SKPoint(region.Bounds.Left, region.Bounds.Bottom),
            },
            RegionShape.OrientedRectangle => region.Points,
            RegionShape.Polygon => region.Points.Count == 4 ? region.Points : Array.Empty<SKPoint>(),
            _ => Array.Empty<SKPoint>(),
        };
    }

    private static void WriteDataYaml(string outputDir)
    {
        var yaml =
            "# Ultralytics YOLO11-OBB データセット記述\n" +
            "# 使い方:\n" +
            "#   yolo task=obb mode=train model=yolo11n-obb.pt data=this_file.yaml epochs=100 imgsz=640\n" +
            "\n" +
            $"path: {outputDir.Replace('\\', '/')}\n" +
            "train: images\n" +
            "val: images   # 少枚数の場合は同じディレクトリで OK (学習スクリプトで自動分割可)\n" +
            "\n" +
            "names:\n" +
            "  0: occlusion\n";
        File.WriteAllText(Path.Combine(outputDir, "data.yaml"), yaml);
    }

    private static void WriteReadme(string outputDir)
    {
        var md =
            "# FantiaMosaic Dataset (YOLO-OBB)\n" +
            "\n" +
            "このデータセットは FantiaMosaic アプリでユーザーが描いた回転矩形を\n" +
            "Ultralytics YOLO11-OBB 用の形式でエクスポートしたものっす。\n" +
            "\n" +
            "## 構造\n" +
            "- `images/` — 画像 (PNG)\n" +
            "- `labels/` — YOLO-OBB ラベル (.txt)\n" +
            "- `data.yaml` — Ultralytics データセット記述\n" +
            "\n" +
            "## ラベル形式\n" +
            "各 .txt の各行は 1 つの領域に対応し、以下の形式:\n" +
            "```\n" +
            "class_id x1 y1 x2 y2 x3 y3 x4 y4\n" +
            "```\n" +
            "座標は画像サイズで normalize された 0-1 の値。クラスは 1 つだけ (`occlusion` = 0)。\n" +
            "\n" +
            "## 学習方法\n" +
            "リポジトリの `tools/train_yolo_obb.py` を参照してくださいっす。\n";
        File.WriteAllText(Path.Combine(outputDir, "README.md"), md);
    }
}
