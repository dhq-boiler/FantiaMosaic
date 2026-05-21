using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FantiaMosaic.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace FantiaMosaic.Services;

/// <summary>
/// Ultralytics YOLO11-OBB の ONNX エクスポートを使って、画像から
/// 回転矩形（隠蔽すべき領域）を推定するディテクタ。
///
/// 入力: NCHW, float32, 0-1 正規化、3チャネル (RGB)
/// 出力 (典型的な YOLO11-OBB ONNX): [1, 4 + nc + 1, N] または [1, N, 4 + nc + 1]
///   座標は 入力画像座標 (imgsz×imgsz の中) の cx, cy, w, h, angle, class_conf, ...
///   Ultralytics の OBB 形式は最後の 1 要素が angle (rad) なので [cx, cy, w, h, conf, angle] のような並びになる。
///
/// 簡略化のため、サポートする出力フォーマット:
///   [1, 6, N]  : cx, cy, w, h, conf, angle  (Ultralytics yolo11n-obb の export)
///   [1, N, 6]  : 上の転置
/// </summary>
public sealed class YoloObbDetector : IDisposable
{
    public sealed class Options
    {
        public string ModelPath { get; set; } = string.Empty;
        public int ImgSize { get; set; } = 640;
        public float ConfidenceThreshold { get; set; } = 0.25f;
        public float NmsIoUThreshold { get; set; } = 0.45f;
        public bool UseGpu { get; set; } = false; // GPU パッケージがある場合のみ有効
    }

    private InferenceSession? _session;
    private Options _opt;

    public YoloObbDetector(Options options)
    {
        _opt = options;
        if (!string.IsNullOrEmpty(options.ModelPath) && File.Exists(options.ModelPath))
            LoadModel(options.ModelPath, options.UseGpu);
    }

    public bool IsLoaded => _session != null;
    public Options CurrentOptions => _opt;
    /// <summary>UseGpu=true でロードした際、実際に CUDA プロバイダが有効になったか。失敗時 CPU にフォールバックされる。</summary>
    public bool IsGpuActive { get; private set; }
    /// <summary>GPU プロバイダの初期化に失敗した場合の説明 (UI 表示用)。成功時は空。</summary>
    public string GpuFallbackReason { get; private set; } = string.Empty;

    public void LoadModel(string modelPath, bool useGpu = false)
    {
        Dispose();
        IsGpuActive = false;
        GpuFallbackReason = string.Empty;

        var so = new SessionOptions();
        if (useGpu)
        {
            try
            {
                so.AppendExecutionProvider_CUDA();
                IsGpuActive = true;
            }
            catch (Exception ex)
            {
                // CUDA プロバイダが無い場合は CPU にフォールバック
                GpuFallbackReason = ex.Message;
                so = new SessionOptions(); // 上の Append が部分的に効いてる可能性を避けるため再生成
            }
        }
        _session = new InferenceSession(modelPath, so);

        // モデルの入力 shape から imgsz を自動取得 (学習時の imgsz と合わせる)
        var detectedImgSize = _opt.ImgSize;
        try
        {
            var inputMeta = _session.InputMetadata.Values.FirstOrDefault();
            if (inputMeta != null)
            {
                var dims = inputMeta.Dimensions; // [N, C, H, W]
                if (dims.Length >= 4 && dims[2] > 0 && dims[3] > 0 && dims[2] == dims[3])
                    detectedImgSize = dims[2];
            }
        }
        catch
        {
            // 形状取得失敗時は既定値のまま
        }

        _opt = new Options
        {
            ModelPath = modelPath,
            ImgSize = detectedImgSize,
            ConfidenceThreshold = _opt.ConfidenceThreshold,
            NmsIoUThreshold = _opt.NmsIoUThreshold,
            UseGpu = useGpu,
        };
    }

    /// <summary>
    /// 画像から回転矩形を検出する。検出が無ければ空のリスト。
    /// </summary>
    public IReadOnlyList<RotatedMosaicDetector.OrientedRect> Detect(SKBitmap bitmap, Options? options = null)
    {
        if (_session == null)
            throw new InvalidOperationException("ONNX モデルが読み込まれていないっす。");
        var opt = options ?? _opt;

        // letterbox リサイズ (アスペクト保持で imgsz×imgsz にパディング)
        var (input, scale, padX, padY) = LetterboxToTensor(bitmap, opt.ImgSize);

        var inputName = _session.InputNames[0];
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        var outTensor = results[0].AsTensor<float>();

        var rects = ParseOutput(outTensor, opt.ConfidenceThreshold);

        // letterbox 座標 → 元画像座標
        var mapped = new List<(RotatedMosaicDetector.OrientedRect rect, float conf)>();
        foreach (var (r, c) in rects)
        {
            var cx = (r.Center.X - padX) / scale;
            var cy = (r.Center.Y - padY) / scale;
            var w = r.Width / scale;
            var h = r.Height / scale;
            mapped.Add((new RotatedMosaicDetector.OrientedRect(new SKPoint(cx, cy), w, h, r.AngleRad), c));
        }

        // 簡易 NMS (AABB IoU ベース)
        var nms = ApplyNms(mapped, opt.NmsIoUThreshold);
        return nms.OrderByDescending(r => r.Width * r.Height).ToList();
    }

    public IReadOnlyList<MosaicRegion> DetectAsRegions(SKBitmap bitmap, Options? options = null)
        => Detect(bitmap, options)
            .Select(r => MosaicRegion.FromOrientedRect(r.Center, r.Width, r.Height, r.AngleRad))
            .ToList();

    // ==================== Internals ====================

    private static (DenseTensor<float> Input, float Scale, float PadX, float PadY) LetterboxToTensor(SKBitmap src, int imgsz)
    {
        var srcW = src.Width;
        var srcH = src.Height;
        var scale = Math.Min((float)imgsz / srcW, (float)imgsz / srcH);
        var newW = (int)MathF.Round(srcW * scale);
        var newH = (int)MathF.Round(srcH * scale);
        var padX = (imgsz - newW) / 2f;
        var padY = (imgsz - newH) / 2f;

        // resize で 中央にパディング
        var info = new SKImageInfo(imgsz, imgsz, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKBitmap(info);
        using (var c = new SKCanvas(canvas))
        {
            c.Clear(new SKColor(114, 114, 114));
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
            c.DrawBitmap(src,
                source: new SKRect(0, 0, srcW, srcH),
                dest: new SKRect(padX, padY, padX + newW, padY + newH),
                paint: paint);
        }

        var tensor = new DenseTensor<float>(new[] { 1, 3, imgsz, imgsz });
        var px = canvas.Pixels;
        var stride = imgsz * imgsz;
        for (var y = 0; y < imgsz; y++)
        for (var x = 0; x < imgsz; x++)
        {
            var c = px[y * imgsz + x];
            var idx = y * imgsz + x;
            tensor.Buffer.Span[0 * stride + idx] = c.Red / 255f;
            tensor.Buffer.Span[1 * stride + idx] = c.Green / 255f;
            tensor.Buffer.Span[2 * stride + idx] = c.Blue / 255f;
        }
        return (tensor, scale, padX, padY);
    }

    private static List<(RotatedMosaicDetector.OrientedRect Rect, float Conf)> ParseOutput(Tensor<float> outTensor, float confThresh)
    {
        var list = new List<(RotatedMosaicDetector.OrientedRect, float)>();
        var dims = outTensor.Dimensions.ToArray();

        // [1, C, N] or [1, N, C]
        int batch = dims[0];
        int a = dims[1];
        int b = dims[2];
        bool channelsFirst = a <= 16 && b > a; // C が小さく、N が大きい
        int featDim = channelsFirst ? a : b;
        int numProposals = channelsFirst ? b : a;

        // Ultralytics yolo11n-obb 単一クラスの場合: 6 ch (cx, cy, w, h, conf, angle)
        // 多クラスの場合: 4 + nc + 1 (last = angle)
        // ここでは class score 列 (4..featDim-2) の最大値を conf とみなす
        for (var i = 0; i < numProposals; i++)
        {
            float cx, cy, w, h, angle;
            float conf = 0;
            if (channelsFirst)
            {
                cx = outTensor[0, 0, i];
                cy = outTensor[0, 1, i];
                w = outTensor[0, 2, i];
                h = outTensor[0, 3, i];
                for (var k = 4; k < featDim - 1; k++)
                {
                    var v = outTensor[0, k, i];
                    if (v > conf) conf = v;
                }
                angle = outTensor[0, featDim - 1, i];
            }
            else
            {
                cx = outTensor[0, i, 0];
                cy = outTensor[0, i, 1];
                w = outTensor[0, i, 2];
                h = outTensor[0, i, 3];
                for (var k = 4; k < featDim - 1; k++)
                {
                    var v = outTensor[0, i, k];
                    if (v > conf) conf = v;
                }
                angle = outTensor[0, i, featDim - 1];
            }

            if (conf < confThresh) continue;
            if (w <= 1 || h <= 1) continue;

            list.Add((new RotatedMosaicDetector.OrientedRect(new SKPoint(cx, cy), w, h, angle), conf));
        }
        return list;
    }

    private static List<RotatedMosaicDetector.OrientedRect> ApplyNms(
        List<(RotatedMosaicDetector.OrientedRect Rect, float Conf)> dets,
        float iouThresh)
    {
        // AABB ベースの簡易 NMS。回転を厳密に扱う SAT-IoU は計算重いため近似。
        var sorted = dets.OrderByDescending(d => d.Conf).ToList();
        var picked = new List<RotatedMosaicDetector.OrientedRect>();
        var pickedAabb = new List<SKRect>();
        foreach (var d in sorted)
        {
            var aabb = ToAabb(d.Rect);
            var skip = false;
            foreach (var pa in pickedAabb)
            {
                if (Iou(aabb, pa) > iouThresh) { skip = true; break; }
            }
            if (skip) continue;
            picked.Add(d.Rect);
            pickedAabb.Add(aabb);
        }
        return picked;
    }

    private static SKRect ToAabb(RotatedMosaicDetector.OrientedRect r)
    {
        var v = r.Vertices();
        float l = float.MaxValue, t = float.MaxValue, rr = float.MinValue, b = float.MinValue;
        foreach (var p in v)
        {
            if (p.X < l) l = p.X;
            if (p.Y < t) t = p.Y;
            if (p.X > rr) rr = p.X;
            if (p.Y > b) b = p.Y;
        }
        return new SKRect(l, t, rr, b);
    }

    private static float Iou(SKRect a, SKRect b)
    {
        var ix = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        var iy = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        var inter = ix * iy;
        var ua = a.Width * a.Height;
        var ub = b.Width * b.Height;
        var union = ua + ub - inter;
        if (union <= 0) return 0;
        return inter / union;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
