using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FantiaMosaic.Models;

namespace FantiaMosaic.Services;

public sealed record BatchProgress(int Done, int Total, string CurrentFile, int FailedCount);

public sealed record BatchResult(int Success, int Failed, IReadOnlyList<string> FailedFiles, TimeSpan Elapsed);

/// <summary>
/// 大量画像を並列でモザイク処理し、進捗を返すバッチエクスポータ。
///
/// 並列数は CPU 数の半分（最低1）で、I/O とデコード負荷のバランスを取る。
/// 元画像は破棄しないので、出力先は必ず別フォルダにする想定。
/// </summary>
public sealed class BatchExporter
{
    private readonly IMosaicEngine _engine;
    private readonly IImageIo _io;

    public BatchExporter(IMosaicEngine engine, IImageIo io)
    {
        _engine = engine;
        _io = io;
    }

    public async Task<BatchResult> ExportAsync(
        IReadOnlyList<ImageJob> jobs,
        MosaicSettings settings,
        string outputFolder,
        IProgress<BatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputFolder))
            throw new ArgumentException("出力先が空っす", nameof(outputFolder));
        Directory.CreateDirectory(outputFolder);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var total = jobs.Count;
        var done = 0;
        var failed = 0;
        var failedFiles = new System.Collections.Concurrent.ConcurrentBag<string>();

        var degree = Math.Max(1, Environment.ProcessorCount / 2);

        await Parallel.ForEachAsync(jobs, new ParallelOptions
        {
            MaxDegreeOfParallelism = degree,
            CancellationToken = cancellationToken,
        }, async (job, ct) =>
        {
            try
            {
                using var bmp = _io.Load(job.SourcePath);
                using var processed = _engine.Apply(bmp, job.Regions.ToList(), settings);

                var name = Path.GetFileNameWithoutExtension(job.SourcePath);
                var ext = Path.GetExtension(job.SourcePath);
                var outPath = Path.Combine(outputFolder, $"{name}{job.OutputSuffix}{ext}");
                _io.Save(processed, outPath);

                if (settings.ThumbnailLongSide > 0)
                {
                    using var thumb = _io.CreateThumbnail(processed, settings.ThumbnailLongSide);
                    var thumbPath = Path.Combine(outputFolder, $"{name}{job.OutputSuffix}_thumb{ext}");
                    _io.Save(thumb, thumbPath);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                Interlocked.Increment(ref failed);
                failedFiles.Add(job.SourcePath);
            }
            finally
            {
                var d = Interlocked.Increment(ref done);
                progress?.Report(new BatchProgress(d, total, Path.GetFileName(job.SourcePath), failed));
            }
            await Task.CompletedTask;
        });

        sw.Stop();
        return new BatchResult(total - failed, failed, failedFiles.ToList(), sw.Elapsed);
    }

    /// <summary>
    /// 指定フォルダ配下の対応画像を再帰的に列挙する。
    /// </summary>
    public static IEnumerable<string> EnumerateImages(string folder, bool recursive)
    {
        if (!Directory.Exists(folder)) yield break;
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".bmp" };
        foreach (var f in Directory.EnumerateFiles(folder, "*.*", opt))
        {
            if (exts.Contains(Path.GetExtension(f)))
                yield return f;
        }
    }
}
