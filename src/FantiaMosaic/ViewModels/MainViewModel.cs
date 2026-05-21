using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FantiaMosaic.Models;
using FantiaMosaic.Services;
using Microsoft.Win32;
using SkiaSharp;

namespace FantiaMosaic.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IMosaicEngine _engine;
    private readonly IImageIo _io;
    private readonly ExistingMosaicDetector _detector;
    private readonly RotatedMosaicDetector _rotatedDetector = new();
    private readonly MosaicGridDetector _gridDetector = new();
    private readonly StripeOcclusionDetector _stripeDetector = new();
    private readonly StripeParameterTuner _tuner = new();
    private CancellationTokenSource? _tuneCts;
    private readonly DatasetExporter _datasetExporter;
    private YoloObbDetector? _yoloDetector;
    private readonly BatchExporter _exporter;
    private readonly SessionStore _sessionStore = new();
    private CancellationTokenSource? _exportCts;

    public ObservableCollection<ImageJobViewModel> Jobs { get; } = new();

    public MosaicSettings Settings { get; } = MosaicSettings.DefaultPreset();

    [ObservableProperty]
    private ImageJobViewModel? selectedJob;

    [ObservableProperty]
    private SKBitmap? currentBitmap;

    [ObservableProperty]
    private string statusText = "画像 or フォルダをドラッグ＆ドロップするっす。";

    [ObservableProperty]
    private string complianceText = string.Empty;

    [ObservableProperty]
    private bool isCompliant = true;

    [ObservableProperty]
    private double relativeStrengthPercent = 3.0;

    [ObservableProperty]
    private int minimumStrengthPx = 16;

    [ObservableProperty]
    private MosaicMode selectedMode = MosaicMode.PixelateThenBlur;

    [ObservableProperty]
    private int thumbnailLongSide = 0;

    [ObservableProperty]
    private string outputFolder = string.Empty;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private double progressMaximum = 1;

    [ObservableProperty]
    private bool isExporting;

    [ObservableProperty]
    private string progressText = string.Empty;

    [ObservableProperty]
    private bool recursiveFolderImport = true;

    // ---- 検出パラメータ (UI で調整可能) ----

    [ObservableProperty]
    private double gridMatchThreshold = 0.32;

    [ObservableProperty]
    private double gridSafetyMargin = 0.0;

    [ObservableProperty]
    private int stripeMinWidthPx = 4;

    [ObservableProperty]
    private int stripeMaxWidthPx = 40;

    [ObservableProperty]
    private int stripeMaxLengthPx = 600;

    [ObservableProperty]
    private int stripeMinPixels = 80;

    [ObservableProperty]
    private double stripeMinShortToLongRatio = 0.7;

    [ObservableProperty]
    private int stripeClosingRadius = 0;

    [ObservableProperty]
    private int stripeOpeningRadius = 2;

    [ObservableProperty]
    private bool isTuning;

    [ObservableProperty]
    private string tuneStatus = string.Empty;

    [ObservableProperty]
    private double tuneProgressValue;

    [ObservableProperty]
    private double tuneProgressMax = 1;

    [ObservableProperty]
    private string yoloModelPath = string.Empty;

    [ObservableProperty]
    private double yoloConfidenceThreshold = 0.25;

    [ObservableProperty]
    private int yoloImgSize = 640;

    [ObservableProperty]
    private bool yoloUseGpu = true;

    public bool YoloLoaded => _yoloDetector?.IsLoaded == true;

    [ObservableProperty]
    private bool isDetecting;

    [ObservableProperty]
    private double detectProgressValue;

    [ObservableProperty]
    private double detectProgressMax = 1;

    [ObservableProperty]
    private string detectStatus = string.Empty;

    private CancellationTokenSource? _detectCts;

    public Array AvailableModes => Enum.GetValues(typeof(MosaicMode));

    // ---- SolidFill 色プリセット ----
    public sealed record SolidColorOption(string Name, SKColor Color);

    public IReadOnlyList<SolidColorOption> SolidColorPresets { get; } = new SolidColorOption[]
    {
        new("黒", SKColors.Black),
        new("白", SKColors.White),
        new("グレー (薄)", new SKColor(220, 220, 220)),
        new("グレー (中)", new SKColor(128, 128, 128)),
        new("グレー (濃)", new SKColor(64, 64, 64)),
        new("ベージュ", new SKColor(245, 222, 179)),
        new("肌色", new SKColor(255, 224, 189)),
        new("ピンク", new SKColor(255, 192, 203)),
    };

    [ObservableProperty]
    private SolidColorOption? selectedSolidColor;

    partial void OnSelectedSolidColorChanged(SolidColorOption? value)
    {
        if (value != null) Settings.SolidColor = value.Color;
    }

    public MainViewModel() : this(new MosaicEngine(), new SkiaImageIo(), new ExistingMosaicDetector())
    {
    }

    public MainViewModel(IMosaicEngine engine, IImageIo io, ExistingMosaicDetector detector)
    {
        _engine = engine;
        _io = io;
        _detector = detector;
        _exporter = new BatchExporter(_engine, _io);
        _datasetExporter = new DatasetExporter(_io);
        // デフォルト選択 (Settings.SolidColor=黒 と同期)
        SelectedSolidColor = SolidColorPresets[0];
    }

    partial void OnSelectedJobChanged(ImageJobViewModel? value)
    {
        CurrentBitmap?.Dispose();
        CurrentBitmap = null;
        if (value == null) return;
        try
        {
            CurrentBitmap = _io.Load(value.Job.SourcePath);
            UpdateCompliance();
        }
        catch (Exception ex)
        {
            StatusText = $"読み込み失敗っす: {ex.Message}";
        }
    }

    partial void OnRelativeStrengthPercentChanged(double value)
    {
        Settings.RelativeStrength = value / 100.0;
        UpdateCompliance();
    }

    partial void OnMinimumStrengthPxChanged(int value)
    {
        Settings.MinimumStrengthPx = value;
        UpdateCompliance();
    }

    partial void OnSelectedModeChanged(MosaicMode value)
    {
        Settings.Mode = value;
    }

    partial void OnThumbnailLongSideChanged(int value)
    {
        Settings.ThumbnailLongSide = value;
    }

    private void UpdateCompliance()
    {
        if (SelectedJob == null) { ComplianceText = string.Empty; IsCompliant = true; return; }
        var r = _engine.CheckCompliance(SelectedJob.Job.OriginalWidth, SelectedJob.Job.OriginalHeight, Settings);
        IsCompliant = r.IsCompliant;
        ComplianceText = r.Detail;
    }

    [RelayCommand]
    public void AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var f in BatchExporter.EnumerateImages(path, RecursiveFolderImport))
                    if (TryAddOne(f)) added++;
                continue;
            }

            if (TryAddOne(path)) added++;
        }

        if (SelectedJob == null && Jobs.Count > 0)
            SelectedJob = Jobs[0];

        StatusText = $"{added} 件追加したっす（合計 {Jobs.Count} 件）。";
    }

    private bool TryAddOne(string path)
    {
        try
        {
            using var bmp = _io.Load(path);
            var job = new ImageJob
            {
                SourcePath = path,
                OriginalWidth = bmp.Width,
                OriginalHeight = bmp.Height,
            };
            Jobs.Add(new ImageJobViewModel(job));
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"{Path.GetFileName(path)} の読み込みに失敗: {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    public void OpenFiles()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.webp;*.bmp",
        };
        if (dlg.ShowDialog() == true)
            AddFiles(dlg.FileNames);
    }

    [RelayCommand]
    public void OpenFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "画像フォルダを選ぶっす",
        };
        if (dlg.ShowDialog() == true)
            AddFiles(new[] { dlg.FolderName });
    }

    [RelayCommand]
    public void RemoveSelected()
    {
        if (SelectedJob == null) return;
        var idx = Jobs.IndexOf(SelectedJob);
        Jobs.Remove(SelectedJob);
        SelectedJob = Jobs.Count == 0 ? null : Jobs[Math.Min(idx, Jobs.Count - 1)];
    }

    [RelayCommand]
    public void ClearAll()
    {
        Jobs.Clear();
        SelectedJob = null;
        StatusText = "全部クリアしたっす。";
    }

    [RelayCommand]
    public void ClearRegions()
    {
        SelectedJob?.Regions.Clear();
    }

    [RelayCommand]
    public void AutoDetectRegions()
    {
        if (SelectedJob == null || CurrentBitmap == null) return;
        var regions = _detector.DetectAsRegions(CurrentBitmap);
        foreach (var r in regions) { StampColor(r); SelectedJob.Regions.Add(r); }
        StatusText = $"検出候補 {regions.Count} 領域追加したっす。手動で削除/調整できるっす。";
    }

    [RelayCommand]
    public void AutoDetectAll()
    {
        // すべての画像に対して既存モザイク検出をかける（時間かかるので注意）。
        var added = 0;
        foreach (var jvm in Jobs)
        {
            try
            {
                using var bmp = _io.Load(jvm.Job.SourcePath);
                var regions = _detector.DetectAsRegions(bmp);
                foreach (var r in regions) { StampColor(r); jvm.Regions.Add(r); }
                added += regions.Count;
            }
            catch { /* skip */ }
        }
        StatusText = $"全画像で {added} 領域検出したっす。";
    }

    [RelayCommand]
    public void AutoDetectRotated()
    {
        // 既存モザイクの「傾いた矩形」領域を自動推定する
        if (SelectedJob == null || CurrentBitmap == null) return;
        var regions = _rotatedDetector.DetectAsRegions(CurrentBitmap);
        foreach (var r in regions) { StampColor(r); SelectedJob.Regions.Add(r); }
        StatusText = $"回転矩形 {regions.Count} 個を検出したっす。";
    }

    private MosaicGridDetector.Options BuildGridOptions() => new()
    {
        MatchThreshold = GridMatchThreshold,
        SafetyMarginRatio = GridSafetyMargin,
    };

    private StripeOcclusionDetector.Options BuildStripeOptions() => new()
    {
        MinStripeWidthPx = StripeMinWidthPx,
        MaxStripeWidthPx = StripeMaxWidthPx,
        MaxStripeLengthPx = StripeMaxLengthPx,
        MinStripePixels = StripeMinPixels,
        MinShortToLongRatio = StripeMinShortToLongRatio,
        ClosingRadius = StripeClosingRadius,
        OpeningRadius = StripeOpeningRadius,
    };

    /// <summary>新規領域に現在の塗りつぶし色をスナップショット保存。</summary>
    private void StampColor(MosaicRegion r) => r.FillColor = Settings.SolidColor;

    [RelayCommand]
    public void AutoDetectGrid()
    {
        // タイル格子モザイク (NCC + PCA) 検出
        if (SelectedJob == null || CurrentBitmap == null) return;
        var regions = _gridDetector.DetectAsRegions(CurrentBitmap, BuildGridOptions());
        foreach (var r in regions) { StampColor(r); SelectedJob.Regions.Add(r); }
        StatusText = $"格子モザイクを {regions.Count} 領域検出したっす。";
    }

    [RelayCommand]
    public void AutoDetectGridAll()
    {
        var added = 0;
        var opt = BuildGridOptions();
        foreach (var jvm in Jobs)
        {
            try
            {
                using var bmp = _io.Load(jvm.Job.SourcePath);
                var regions = _gridDetector.DetectAsRegions(bmp, opt);
                foreach (var r in regions) { StampColor(r); jvm.Regions.Add(r); }
                added += regions.Count;
            }
            catch { /* skip */ }
        }
        StatusText = $"全画像で格子モザイクを {added} 領域検出したっす。";
    }

    [RelayCommand]
    public async Task TuneStripeParametersAsync()
    {
        if (SelectedJob == null || CurrentBitmap == null)
        {
            StatusText = "画像を選んでくださいっす。";
            return;
        }
        if (SelectedJob.Regions.Count == 0)
        {
            StatusText = "先に正解領域を矩形で描いてくださいっす (ハンドル編集で位置・サイズ・角度を調整可能)。";
            return;
        }

        var gt = SelectedJob.Regions.ToList();
        var bitmap = CurrentBitmap;
        var seed = BuildStripeOptions();

        IsTuning = true;
        TuneStatus = "探索を準備中っす…";
        TuneProgressValue = 0;
        _tuneCts = new CancellationTokenSource();

        var progress = new Progress<StripeParameterTuner.Progress>(p =>
        {
            TuneProgressValue = p.Done;
            TuneProgressMax = Math.Max(1, p.Total);
            TuneStatus = $"探索中 {p.Done}/{p.Total}  best IoU = {p.CurrentBestScore:F3}";
        });

        try
        {
            var result = await _tuner.TuneAsync(bitmap, gt, seed, progress, _tuneCts.Token);

            // 探索結果を UI パラメータに反映
            var o = result.BestOptions;
            StripeMinWidthPx = o.MinStripeWidthPx;
            StripeMaxWidthPx = o.MaxStripeWidthPx;
            StripeMaxLengthPx = o.MaxStripeLengthPx;
            StripeMinPixels = o.MinStripePixels;
            StripeMinShortToLongRatio = o.MinShortToLongRatio;
            StripeOpeningRadius = o.OpeningRadius;
            StripeClosingRadius = o.ClosingRadius;

            StatusText = $"探索完了っす: IoU={result.BestScore:F3} / 評価回数={result.Evaluations} / 所要 {result.Elapsed.TotalSeconds:F1}s";
            TuneStatus = $"完了: best IoU = {result.BestScore:F3}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "探索を中断したっす。";
            TuneStatus = "中断";
        }
        catch (Exception ex)
        {
            StatusText = $"探索失敗っす: {ex.Message}";
            TuneStatus = "エラー";
        }
        finally
        {
            IsTuning = false;
            _tuneCts?.Dispose();
            _tuneCts = null;
        }
    }

    [RelayCommand]
    public void CancelTune()
    {
        _tuneCts?.Cancel();
    }

    /// <summary>
    /// GT (正解矩形) を持つ全画像を学習データとしてパラメータ最適化する。
    /// </summary>
    [RelayCommand]
    public async Task TuneStripeFromAllSamplesAsync()
    {
        // GT 付き画像を集める (各画像の Regions が空でないもの)
        var samples = new List<StripeParameterTuner.Sample>();
        var loadedBitmaps = new List<SKBitmap>();
        try
        {
            foreach (var jvm in Jobs)
            {
                if (jvm.Regions.Count == 0) continue;
                SKBitmap bmp;
                if (ReferenceEquals(jvm, SelectedJob) && CurrentBitmap != null)
                {
                    bmp = CurrentBitmap; // 表示中はキャッシュ済み
                }
                else
                {
                    bmp = _io.Load(jvm.Job.SourcePath);
                    loadedBitmaps.Add(bmp);
                }
                samples.Add(new StripeParameterTuner.Sample(bmp, jvm.Regions.ToList()));
            }

            if (samples.Count == 0)
            {
                StatusText = "GT 付き画像が無いっす。各画像に正解矩形を描いてから実行してくださいっす。";
                return;
            }

            var seed = BuildStripeOptions();
            IsTuning = true;
            TuneStatus = $"探索を準備中っす… (学習サンプル: {samples.Count} 枚)";
            TuneProgressValue = 0;
            _tuneCts = new CancellationTokenSource();

            var progress = new Progress<StripeParameterTuner.Progress>(p =>
            {
                TuneProgressValue = p.Done;
                TuneProgressMax = Math.Max(1, p.Total);
                TuneStatus = $"探索中 {p.Done}/{p.Total}  avg IoU = {p.CurrentBestScore:F3}  サンプル {samples.Count} 枚";
            });

            var result = await _tuner.TuneMultiAsync(samples, seed, progress, _tuneCts.Token);

            var o = result.BestOptions;
            StripeMinWidthPx = o.MinStripeWidthPx;
            StripeMaxWidthPx = o.MaxStripeWidthPx;
            StripeMaxLengthPx = o.MaxStripeLengthPx;
            StripeMinPixels = o.MinStripePixels;
            StripeMinShortToLongRatio = o.MinShortToLongRatio;
            StripeOpeningRadius = o.OpeningRadius;
            StripeClosingRadius = o.ClosingRadius;

            StatusText = $"複数画像探索完了っす: avg IoU={result.BestScore:F3} / {samples.Count}枚 / 評価 {result.Evaluations}回 / 所要 {result.Elapsed.TotalSeconds:F1}s";
            TuneStatus = $"完了: avg IoU = {result.BestScore:F3}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "探索を中断したっす。";
            TuneStatus = "中断";
        }
        catch (Exception ex)
        {
            StatusText = $"探索失敗っす: {ex.Message}";
            TuneStatus = "エラー";
        }
        finally
        {
            // 一時的にロードしたビットマップを破棄 (表示中のものは破棄しない)
            foreach (var bmp in loadedBitmaps) bmp.Dispose();
            IsTuning = false;
            _tuneCts?.Dispose();
            _tuneCts = null;
        }
    }

    [RelayCommand]
    public void AutoDetectStripes()
    {
        // 棒線隠蔽 (平行棒線群クラスタリング) 検出
        if (SelectedJob == null || CurrentBitmap == null) return;
        var regions = _stripeDetector.DetectAsRegions(CurrentBitmap, BuildStripeOptions());
        foreach (var r in regions) { StampColor(r); SelectedJob.Regions.Add(r); }
        StatusText = $"棒線隠蔽を {regions.Count} 領域検出したっす。";
    }

    [RelayCommand]
    public void AutoDetectStripesAll()
    {
        var added = 0;
        var opt = BuildStripeOptions();
        foreach (var jvm in Jobs)
        {
            try
            {
                using var bmp = _io.Load(jvm.Job.SourcePath);
                var regions = _stripeDetector.DetectAsRegions(bmp, opt);
                foreach (var r in regions) { StampColor(r); jvm.Regions.Add(r); }
                added += regions.Count;
            }
            catch { /* skip */ }
        }
        StatusText = $"全画像で棒線隠蔽を {added} 領域検出したっす。";
    }

    [RelayCommand]
    public void AutoDetectRotatedAll()
    {
        var added = 0;
        foreach (var jvm in Jobs)
        {
            try
            {
                using var bmp = _io.Load(jvm.Job.SourcePath);
                var regions = _rotatedDetector.DetectAsRegions(bmp);
                foreach (var r in regions) { StampColor(r); jvm.Regions.Add(r); }
                added += regions.Count;
            }
            catch { /* skip */ }
        }
        StatusText = $"全画像で回転矩形 {added} 個を検出したっす。";
    }

    [RelayCommand]
    public void CopyRegionsToAll()
    {
        if (SelectedJob == null) return;
        var src = SelectedJob.Regions.ToList();
        foreach (var j in Jobs)
        {
            if (j == SelectedJob) continue;
            j.Regions.Clear();
            foreach (var r in src) j.Regions.Add(CloneRegion(r));
        }
        StatusText = "他の全画像に領域コピーしたっす。";
    }

    [RelayCommand]
    public void ApplyPresetDefault()
    {
        SelectedMode = MosaicMode.PixelateThenBlur;
        RelativeStrengthPercent = 3.0;
        MinimumStrengthPx = 16;
        Settings.PostBlurRatio = 0.25;
    }

    [RelayCommand]
    public void ApplyPresetStrong()
    {
        SelectedMode = MosaicMode.PixelateThenBlur;
        RelativeStrengthPercent = 5.0;
        MinimumStrengthPx = 24;
        Settings.PostBlurRatio = 0.4;
    }

    [RelayCommand]
    public void ChooseOutputFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "出力先フォルダを選ぶっす",
        };
        if (dlg.ShowDialog() == true)
            OutputFolder = dlg.FolderName;
    }

    [RelayCommand]
    public async Task ExportAllAsync()
    {
        if (Jobs.Count == 0) { StatusText = "画像が無いっす。"; return; }
        if (string.IsNullOrWhiteSpace(OutputFolder) || !Directory.Exists(OutputFolder))
        {
            ChooseOutputFolder();
            if (string.IsNullOrWhiteSpace(OutputFolder)) return;
        }

        var jobs = Jobs.Select(j => j.Job).ToList();
        // 入力フォルダと出力先が同じだと元画像を上書きする恐れがあるので警告。
        var anySameFolder = jobs.Any(j =>
            string.Equals(Path.GetDirectoryName(j.SourcePath), OutputFolder, StringComparison.OrdinalIgnoreCase));
        if (anySameFolder)
        {
            var ok = MessageBox.Show(
                "出力先が元画像と同じフォルダのジョブがあるっす。サフィックスで衝突は避けるっすけど、本当に続けるっすか？",
                "確認", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;
        }

        _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ProgressValue = 0;
        ProgressMaximum = jobs.Count;
        ProgressText = $"0 / {jobs.Count}";
        StatusText = "エクスポート中っす…";

        var progress = new Progress<BatchProgress>(p =>
        {
            ProgressValue = p.Done;
            ProgressMaximum = p.Total;
            ProgressText = $"{p.Done} / {p.Total} ({p.CurrentFile})";
        });

        try
        {
            var result = await _exporter.ExportAsync(jobs, Settings, OutputFolder, progress, _exportCts.Token);
            StatusText = $"完了っす: 成功 {result.Success} 件 / 失敗 {result.Failed} 件 / 所要 {result.Elapsed.TotalSeconds:F1}s";
        }
        catch (OperationCanceledException)
        {
            StatusText = "中断したっす。";
        }
        finally
        {
            IsExporting = false;
            _exportCts?.Dispose();
            _exportCts = null;
        }
    }

    [RelayCommand]
    public void CancelExport()
    {
        _exportCts?.Cancel();
    }

    [RelayCommand]
    public void LoadYoloModel()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "ONNX モデル|*.onnx",
            Title = "YOLO11-OBB の ONNX モデルを選ぶっす",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _yoloDetector?.Dispose();
            _yoloDetector = new YoloObbDetector(new YoloObbDetector.Options
            {
                ModelPath = dlg.FileName,
                ImgSize = YoloImgSize,
                ConfidenceThreshold = (float)YoloConfidenceThreshold,
                UseGpu = YoloUseGpu,
            });
            // モデルから取得された入力サイズを UI に反映 (学習時の imgsz と一致させるため)
            var detectedSize = _yoloDetector.CurrentOptions.ImgSize;
            if (detectedSize != YoloImgSize) YoloImgSize = detectedSize;

            YoloModelPath = dlg.FileName;
            OnPropertyChanged(nameof(YoloLoaded));

            // 実際に GPU が有効になったかをユーザーに伝える
            string device;
            if (!YoloUseGpu) device = "CPU";
            else if (_yoloDetector.IsGpuActive) device = "GPU(CUDA)";
            else device = $"CPU (GPU失敗: {_yoloDetector.GpuFallbackReason})";

            StatusText = $"DLモデル読み込み成功っす: {Path.GetFileName(dlg.FileName)} (imgsz={detectedSize}, {device})";
        }
        catch (Exception ex)
        {
            StatusText = $"モデル読み込み失敗っす: {ex.Message}";
        }
    }

    [RelayCommand]
    public void AutoDetectYolo()
    {
        if (_yoloDetector == null || !_yoloDetector.IsLoaded)
        {
            StatusText = "先に DLモデル(.onnx) を読み込んでくださいっす。";
            return;
        }
        if (SelectedJob == null || CurrentBitmap == null) return;
        try
        {
            var opt = new YoloObbDetector.Options
            {
                ModelPath = YoloModelPath,
                ImgSize = _yoloDetector.CurrentOptions.ImgSize, // モデル側を尊重 (mismatch 回避)
                ConfidenceThreshold = (float)YoloConfidenceThreshold,
                UseGpu = YoloUseGpu,
            };
            var regions = _yoloDetector.DetectAsRegions(CurrentBitmap, opt);
            foreach (var r in regions) { StampColor(r); SelectedJob.Regions.Add(r); }
            StatusText = $"DL検出で {regions.Count} 領域追加したっす。";
        }
        catch (Exception ex)
        {
            StatusText = $"DL検出失敗っす: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task AutoDetectYoloAllAsync()
    {
        if (_yoloDetector == null || !_yoloDetector.IsLoaded)
        {
            StatusText = "先に DLモデル(.onnx) を読み込んでくださいっす。";
            return;
        }
        if (Jobs.Count == 0)
        {
            StatusText = "画像が無いっす。";
            return;
        }

        var opt = new YoloObbDetector.Options
        {
            ModelPath = YoloModelPath,
            ImgSize = _yoloDetector.CurrentOptions.ImgSize, // モデル側を尊重 (mismatch 回避)
            ConfidenceThreshold = (float)YoloConfidenceThreshold,
            UseGpu = YoloUseGpu,
        };
        var jobs = Jobs.ToList();
        var detector = _yoloDetector;
        var dispatcher = System.Windows.Application.Current.Dispatcher;

        IsDetecting = true;
        DetectProgressValue = 0;
        DetectProgressMax = jobs.Count;
        DetectStatus = $"DL検出 開始… ({jobs.Count} 枚)";
        _detectCts = new CancellationTokenSource();
        StatusText = "DL検出 中っす…";
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var added = await Task.Run(() =>
            {
                var total = 0;
                for (var i = 0; i < jobs.Count; i++)
                {
                    _detectCts.Token.ThrowIfCancellationRequested();
                    var jvm = jobs[i];
                    var fname = Path.GetFileName(jvm.Job.SourcePath);
                    try
                    {
                        using var bmp = _io.Load(jvm.Job.SourcePath);
                        var regions = detector.DetectAsRegions(bmp, opt);
                        // UI スレッドで Regions を更新 (ObservableCollection は STA で操作)
                        dispatcher.Invoke(() =>
                        {
                            foreach (var r in regions) { StampColor(r); jvm.Regions.Add(r); }
                        });
                        Interlocked.Add(ref total, regions.Count);
                    }
                    catch { /* skip */ }

                    var done = i + 1;
                    dispatcher.Invoke(() =>
                    {
                        DetectProgressValue = done;
                        DetectStatus = $"{done} / {jobs.Count}  ({fname})";
                    });
                }
                return total;
            }, _detectCts.Token);

            sw.Stop();
            StatusText = $"DL検出 完了っす: {added} 領域追加 / {jobs.Count} 枚 / 所要 {sw.Elapsed.TotalSeconds:F1}s";
            DetectStatus = $"完了: {added} 領域";
        }
        catch (OperationCanceledException)
        {
            StatusText = "DL検出を中断したっす。";
            DetectStatus = "中断";
        }
        catch (Exception ex)
        {
            StatusText = $"DL検出失敗っす: {ex.Message}";
            DetectStatus = "エラー";
        }
        finally
        {
            IsDetecting = false;
            _detectCts?.Dispose();
            _detectCts = null;
        }
    }

    [RelayCommand]
    public void CancelDetect()
    {
        _detectCts?.Cancel();
    }

    [RelayCommand]
    public async Task ExportDatasetAsync()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "YOLO-OBB データセットの出力先フォルダを選ぶっす",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var jobs = Jobs.Select(j => j.Job).ToList();
            StatusText = "データセットをエクスポート中っす…";
            var result = await _datasetExporter.ExportAsync(jobs, dlg.FolderName);
            StatusText = $"データセット出力完了: 画像 {result.ImagesExported} 枚 / ラベル {result.LabelsExported} 個 → {result.OutputDir}";
        }
        catch (Exception ex)
        {
            StatusText = $"エクスポート失敗っす: {ex.Message}";
        }
    }

    [RelayCommand]
    public void SaveSession()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "FantiaMosaic セッション|*.fmsession.json",
            FileName = $"session_{DateTime.Now:yyyyMMdd_HHmmss}.fmsession.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _sessionStore.Save(dlg.FileName, Jobs.Select(j => j.Job), Settings, OutputFolder);
            StatusText = $"セッション保存したっす: {Path.GetFileName(dlg.FileName)}";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失敗っす: {ex.Message}";
        }
    }

    [RelayCommand]
    public void MergeSession()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "FantiaMosaic セッション|*.fmsession.json",
            Title = "マージするセッションファイルを選ぶっす",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var (newJobs, _, _) = _sessionStore.Load(dlg.FileName);
            var jobsAdded = 0;
            var jobsMerged = 0;
            var regionsAdded = 0;
            foreach (var newJob in newJobs)
            {
                // 同じ SourcePath の既存ジョブを探す
                var existing = Jobs.FirstOrDefault(j =>
                    string.Equals(j.Job.SourcePath, newJob.SourcePath, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    // 領域だけ追加 (重複検知はせず単純結合)
                    foreach (var r in newJob.Regions)
                    {
                        existing.Regions.Add(r);
                        regionsAdded++;
                    }
                    jobsMerged++;
                }
                else
                {
                    // ジョブ自体が無い場合は新規追加
                    if (newJob.OriginalWidth == 0 || newJob.OriginalHeight == 0)
                    {
                        try
                        {
                            using var bmp = _io.Load(newJob.SourcePath);
                            newJob.OriginalWidth = bmp.Width;
                            newJob.OriginalHeight = bmp.Height;
                        }
                        catch { /* keep zeros */ }
                    }
                    Jobs.Add(new ImageJobViewModel(newJob));
                    jobsAdded++;
                    regionsAdded += newJob.Regions.Count;
                }
            }
            if (SelectedJob == null && Jobs.Count > 0) SelectedJob = Jobs[0];
            StatusText = $"マージ完了っす: 新規 {jobsAdded} ジョブ / 既存統合 {jobsMerged} ジョブ / 領域追加 {regionsAdded} 個";
        }
        catch (Exception ex)
        {
            StatusText = $"マージ失敗っす: {ex.Message}";
        }
    }

    [RelayCommand]
    public void LoadSession()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "FantiaMosaic セッション|*.fmsession.json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var (jobs, settings, outputFolder) = _sessionStore.Load(dlg.FileName);
            Jobs.Clear();
            foreach (var j in jobs)
            {
                // 元ファイルが消えていてもジョブだけは復元する。サイズが0だと処理時に困るので最低限のフォールバック。
                if (j.OriginalWidth == 0 || j.OriginalHeight == 0)
                {
                    try
                    {
                        using var bmp = _io.Load(j.SourcePath);
                        j.OriginalWidth = bmp.Width;
                        j.OriginalHeight = bmp.Height;
                    }
                    catch { /* keep zeros */ }
                }
                Jobs.Add(new ImageJobViewModel(j));
            }
            OutputFolder = outputFolder;
            Settings.Mode = settings.Mode;
            Settings.RelativeStrength = settings.RelativeStrength;
            Settings.MinimumStrengthPx = settings.MinimumStrengthPx;
            Settings.PostBlurRatio = settings.PostBlurRatio;
            Settings.ThumbnailLongSide = settings.ThumbnailLongSide;
            Settings.SolidColor = settings.SolidColor;
            RelativeStrengthPercent = settings.RelativeStrength * 100;
            MinimumStrengthPx = settings.MinimumStrengthPx;
            SelectedMode = settings.Mode;
            ThumbnailLongSide = settings.ThumbnailLongSide;

            if (Jobs.Count > 0) SelectedJob = Jobs[0];
            StatusText = $"セッション読み込んだっす: {Jobs.Count} 件";
        }
        catch (Exception ex)
        {
            StatusText = $"読み込み失敗っす: {ex.Message}";
        }
    }

    private static MosaicRegion CloneRegion(MosaicRegion src) => src.Shape switch
    {
        RegionShape.Rectangle => MosaicRegion.FromRect(src.Bounds),
        RegionShape.Ellipse => MosaicRegion.FromEllipse(src.Bounds),
        RegionShape.Polygon => MosaicRegion.FromPolygon(src.Points.ToList()),
        _ => MosaicRegion.FromRect(src.Bounds),
    };
}
