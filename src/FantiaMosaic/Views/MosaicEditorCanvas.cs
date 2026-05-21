using System;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using FantiaMosaic.Models;
using FantiaMosaic.Services;
using FantiaMosaic.ViewModels;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace FantiaMosaic.Views;

/// <summary>
/// 画像と領域を表示・編集する Skia 描画コントロール。
///
/// 操作:
///   - 何も無いところで左ドラッグ → 矩形領域を追加
///   - Shift+左ドラッグ → 楕円領域を追加
///   - 領域内を左クリック → 選択 (回転矩形なら 4隅+回転ハンドル付き)
///   - 選択中の領域中央をドラッグ → 平行移動
///   - 選択中の4隅ハンドルをドラッグ → リサイズ
///   - 選択中の回転ハンドル (上の浮いた点) をドラッグ → 回転
///   - Delete キー → 選択中の領域削除
///   - 右クリック → 矩形外なら何もしない、矩形上なら削除
///   - 中ドラッグ → パン
///   - ホイール → ズーム
/// </summary>
public sealed class MosaicEditorCanvas : SKElement
{
    public static readonly DependencyProperty BitmapProperty =
        DependencyProperty.Register(nameof(Bitmap), typeof(SKBitmap), typeof(MosaicEditorCanvas),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty JobProperty =
        DependencyProperty.Register(nameof(Job), typeof(ImageJobViewModel), typeof(MosaicEditorCanvas),
            new PropertyMetadata(null, OnJobChanged));

    public static readonly DependencyProperty SettingsProperty =
        DependencyProperty.Register(nameof(Settings), typeof(MosaicSettings), typeof(MosaicEditorCanvas),
            new PropertyMetadata(null, OnVisualPropertyChanged));

    public static readonly DependencyProperty ShowPreviewProperty =
        DependencyProperty.Register(nameof(ShowPreview), typeof(bool), typeof(MosaicEditorCanvas),
            new PropertyMetadata(true, OnVisualPropertyChanged));

    public SKBitmap? Bitmap { get => (SKBitmap?)GetValue(BitmapProperty); set => SetValue(BitmapProperty, value); }
    public ImageJobViewModel? Job { get => (ImageJobViewModel?)GetValue(JobProperty); set => SetValue(JobProperty, value); }
    public MosaicSettings? Settings { get => (MosaicSettings?)GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
    public bool ShowPreview { get => (bool)GetValue(ShowPreviewProperty); set => SetValue(ShowPreviewProperty, value); }

    private readonly MosaicEngine _engine = new();
    private SKBitmap? _previewBitmap;

    private float _zoom = 1f;
    private SKPoint _pan = SKPoint.Empty;
    private bool _isPanning;
    private System.Windows.Point _lastMouse;

    // 矩形描画中のドラフト
    private SKPoint? _dragStartImage;
    private SKPoint? _dragEndImage;
    private bool _isEllipseDrag;

    // 選択中の region とハンドル操作
    private MosaicRegion? _selectedRegion;
    private enum HandleKind { None, Move, Resize, Rotate }
    private HandleKind _activeHandle = HandleKind.None;
    private int _activeCornerIndex = -1; // 0:LT, 1:RT, 2:RB, 3:LB
    private SKPoint _grabAnchor;          // 操作開始時の image 座標
    private float _grabInitWidth, _grabInitHeight, _grabInitAngle;
    private SKPoint _grabInitCenter;

    public MosaicEditorCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        MouseLeftButtonDown += OnMouseLeftDown;
        MouseLeftButtonUp += OnMouseLeftUp;
        MouseRightButtonDown += OnMouseRightDown;
        MouseMove += OnMouseMove;
        MouseWheel += OnMouseWheel;
        MouseDown += OnMouseAnyDown;
        MouseUp += OnMouseAnyUp;
        KeyDown += OnKeyDown;
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MosaicEditorCanvas c)
        {
            // Bitmap が変わったらビューポートをフィットし直す
            if (e.Property == BitmapProperty)
                c.FitToView();
            c.InvalidatePreview();
            c.InvalidateVisual();
        }
    }

    private static void OnJobChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MosaicEditorCanvas c) return;
        if (e.OldValue is ImageJobViewModel oldJob)
            oldJob.Regions.CollectionChanged -= c.OnRegionsChanged;
        if (e.NewValue is ImageJobViewModel newJob)
            newJob.Regions.CollectionChanged += c.OnRegionsChanged;
        c._selectedRegion = null;
        c.ResetView();
        c.InvalidatePreview();
        c.InvalidateVisual();
    }

    private void OnRegionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var o in e.OldItems)
                if (ReferenceEquals(o, _selectedRegion)) _selectedRegion = null;
        }
        InvalidatePreview();
        InvalidateVisual();
    }

    private void InvalidatePreview()
    {
        _previewBitmap?.Dispose();
        _previewBitmap = null;
    }

    private void ResetView()
    {
        FitToView();
    }

    /// <summary>画像全体がビューポートに収まるよう自動ズーム+センター配置。</summary>
    public void FitToView()
    {
        if (Bitmap == null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            _zoom = 1f;
            _pan = SKPoint.Empty;
            InvalidateVisual();
            return;
        }
        var sx = (float)ActualWidth / Bitmap.Width;
        var sy = (float)ActualHeight / Bitmap.Height;
        _zoom = Math.Min(sx, sy);
        _pan = new SKPoint(
            (float)((ActualWidth - Bitmap.Width * _zoom) / 2),
            (float)((ActualHeight - Bitmap.Height * _zoom) / 2));
        InvalidateVisual();
    }

    /// <summary>等倍 (100%) で中央配置。</summary>
    public void ResetToOriginalSize()
    {
        if (Bitmap == null) return;
        _zoom = 1f;
        _pan = new SKPoint(
            (float)((ActualWidth - Bitmap.Width) / 2),
            (float)((ActualHeight - Bitmap.Height) / 2));
        InvalidateVisual();
    }

    /// <summary>ビューポートの中心位置で指定倍率にズーム。</summary>
    public void ZoomBy(float factor)
    {
        if (Bitmap == null) return;
        var center = new System.Windows.Point(ActualWidth / 2, ActualHeight / 2);
        var before = ScreenToImage(center);
        _zoom *= factor;
        _zoom = Math.Clamp(_zoom, 0.05f, 20f);
        var after = ScreenToImage(center);
        _pan = new SKPoint(
            _pan.X + (after.X - before.X) * _zoom,
            _pan.Y + (after.Y - before.Y) * _zoom);
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_zoom == 0 || (_zoom == 1f && _pan == SKPoint.Empty)) ResetView();
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x33, 0x33, 0x33));
        if (Bitmap == null) return;

        if (ShowPreview && Job != null && Settings != null)
        {
            if (_previewBitmap == null)
                _previewBitmap = _engine.Apply(Bitmap, Job.Regions.ToList(), Settings);
            DrawBitmap(canvas, _previewBitmap);
        }
        else
        {
            DrawBitmap(canvas, Bitmap);
        }

        if (Job != null)
        {
            DrawRegionOverlays(canvas);
            DrawSelectionHandles(canvas);
        }
        DrawDraftRect(canvas);
    }

    private void DrawBitmap(SKCanvas canvas, SKBitmap bmp)
    {
        canvas.Save();
        canvas.Translate(_pan.X, _pan.Y);
        canvas.Scale(_zoom);
        canvas.DrawBitmap(bmp, 0, 0);
        canvas.Restore();
    }

    private void DrawRegionOverlays(SKCanvas canvas)
    {
        if (Job == null) return;
        canvas.Save();
        canvas.Translate(_pan.X, _pan.Y);
        canvas.Scale(_zoom);
        using var stroke = new SKPaint
        {
            Color = new SKColor(0xFF, 0xC0, 0x40),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f / _zoom,
            IsAntialias = true,
        };
        using var fill = new SKPaint
        {
            Color = new SKColor(0xFF, 0xC0, 0x40, 0x33),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };
        using var selStroke = new SKPaint
        {
            Color = new SKColor(0x40, 0xC0, 0xFF),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f / _zoom,
            IsAntialias = true,
        };

        foreach (var r in Job.Regions)
        {
            var thisStroke = ReferenceEquals(r, _selectedRegion) ? selStroke : stroke;
            switch (r.Shape)
            {
                case RegionShape.Rectangle:
                    canvas.DrawRect(r.Bounds, fill);
                    canvas.DrawRect(r.Bounds, thisStroke);
                    break;
                case RegionShape.Ellipse:
                    canvas.DrawOval(r.Bounds, fill);
                    canvas.DrawOval(r.Bounds, thisStroke);
                    break;
                case RegionShape.Polygon:
                case RegionShape.OrientedRectangle:
                    using (var path = new SKPath())
                    {
                        if (r.Points.Count >= 3)
                        {
                            path.MoveTo(r.Points[0]);
                            for (var i = 1; i < r.Points.Count; i++)
                                path.LineTo(r.Points[i]);
                            path.Close();
                            canvas.DrawPath(path, fill);
                            canvas.DrawPath(path, thisStroke);
                        }
                    }
                    break;
            }
        }
        canvas.Restore();
    }

    private void DrawSelectionHandles(SKCanvas canvas)
    {
        if (_selectedRegion is not { Shape: RegionShape.OrientedRectangle } r) return;

        canvas.Save();
        canvas.Translate(_pan.X, _pan.Y);
        canvas.Scale(_zoom);

        var handleSize = 8f / _zoom;
        using var handleFill = new SKPaint
        {
            Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true,
        };
        using var handleStroke = new SKPaint
        {
            Color = new SKColor(0x40, 0xC0, 0xFF), Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f / _zoom, IsAntialias = true,
        };
        using var rotHandleFill = new SKPaint
        {
            Color = new SKColor(0xFF, 0x80, 0x40), Style = SKPaintStyle.Fill, IsAntialias = true,
        };

        // 4 corner handles
        for (var i = 0; i < r.Points.Count && i < 4; i++)
        {
            var p = r.Points[i];
            canvas.DrawCircle(p, handleSize, handleFill);
            canvas.DrawCircle(p, handleSize, handleStroke);
        }
        // 中央ハンドル (移動)
        canvas.DrawCircle(r.Center, handleSize, handleFill);
        canvas.DrawCircle(r.Center, handleSize, handleStroke);

        // 回転ハンドル: 矩形の「上辺中央」から外側へ伸びる位置
        var rot = GetRotationHandlePosition(r);
        canvas.DrawLine(MidPoint(r.Points[0], r.Points[1]), rot, handleStroke);
        canvas.DrawCircle(rot, handleSize, rotHandleFill);
        canvas.DrawCircle(rot, handleSize, handleStroke);

        canvas.Restore();
    }

    private static SKPoint MidPoint(SKPoint a, SKPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private SKPoint GetRotationHandlePosition(MosaicRegion r)
    {
        // 上辺中央から短辺の 25% 離れた位置
        var mid = MidPoint(r.Points[0], r.Points[1]);
        var dx = mid.X - r.Center.X;
        var dy = mid.Y - r.Center.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3f) return mid;
        var stepLen = Math.Max(20f / _zoom, r.Height * 0.15f);
        return new SKPoint(mid.X + dx / len * stepLen, mid.Y + dy / len * stepLen);
    }

    private void DrawDraftRect(SKCanvas canvas)
    {
        if (_dragStartImage is not { } s || _dragEndImage is not { } eP) return;
        var rect = NormalizeRect(s, eP);
        canvas.Save();
        canvas.Translate(_pan.X, _pan.Y);
        canvas.Scale(_zoom);
        using var paint = new SKPaint
        {
            Color = new SKColor(0x40, 0xC0, 0xFF),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f / _zoom,
            PathEffect = SKPathEffect.CreateDash(new[] { 6f / _zoom, 4f / _zoom }, 0),
        };
        if (_isEllipseDrag) canvas.DrawOval(rect, paint);
        else canvas.DrawRect(rect, paint);
        canvas.Restore();
    }

    private SKPoint ScreenToImage(System.Windows.Point p) => new(
        (float)((p.X - _pan.X) / _zoom),
        (float)((p.Y - _pan.Y) / _zoom));

    private void OnMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (Bitmap == null || Job == null) return;
        Focus();
        var img = ScreenToImage(e.GetPosition(this));

        // 1) 既存選択中の region のハンドルを優先
        if (_selectedRegion is { Shape: RegionShape.OrientedRectangle } selected)
        {
            var handle = HitTestHandle(selected, img);
            if (handle.kind != HandleKind.None)
            {
                _activeHandle = handle.kind;
                _activeCornerIndex = handle.cornerIndex;
                _grabAnchor = img;
                _grabInitCenter = selected.Center;
                _grabInitWidth = selected.Width;
                _grabInitHeight = selected.Height;
                _grabInitAngle = selected.AngleRad;
                CaptureMouse();
                return;
            }
        }

        // 2) クリック位置にある region (後勝ち) を選択
        MosaicRegion? hit = null;
        for (var i = Job.Regions.Count - 1; i >= 0; i--)
        {
            if (RegionContains(Job.Regions[i], img))
            {
                hit = Job.Regions[i];
                break;
            }
        }
        if (hit != null)
        {
            _selectedRegion = hit;
            if (hit.Shape == RegionShape.OrientedRectangle)
            {
                _activeHandle = HandleKind.Move;
                _grabAnchor = img;
                _grabInitCenter = hit.Center;
                _grabInitWidth = hit.Width;
                _grabInitHeight = hit.Height;
                _grabInitAngle = hit.AngleRad;
                CaptureMouse();
            }
            InvalidateVisual();
            return;
        }

        // 3) 何もない場所 → 新規矩形のドラッグ開始
        _selectedRegion = null;
        _isEllipseDrag = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        _dragStartImage = img;
        _dragEndImage = img;
        CaptureMouse();
        InvalidateVisual();
    }

    private void OnMouseLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStartImage is { } s && _dragEndImage is { } eP && Job != null && _activeHandle == HandleKind.None)
        {
            var rect = NormalizeRect(s, eP);
            if (rect.Width >= 4 && rect.Height >= 4)
            {
                MosaicRegion region;
                if (_isEllipseDrag)
                {
                    region = MosaicRegion.FromEllipse(rect);
                }
                else
                {
                    // 描いた直後からハンドルで回転・リサイズ・移動できるよう OrientedRectangle で作る
                    region = MosaicRegion.FromOrientedRect(
                        new SKPoint(rect.MidX, rect.MidY),
                        rect.Width,
                        rect.Height,
                        0f);
                }
                // 現在の塗りつぶし色をスナップショット (後で色を変えても影響しないように)
                if (Settings != null)
                    region.FillColor = Settings.SolidColor;
                Job.Regions.Add(region);
                _selectedRegion = region;
            }
        }
        _dragStartImage = null;
        _dragEndImage = null;
        _activeHandle = HandleKind.None;
        _activeCornerIndex = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidatePreview();
        InvalidateVisual();
    }

    private void OnMouseRightDown(object sender, MouseButtonEventArgs e)
    {
        if (Job == null) return;
        var img = ScreenToImage(e.GetPosition(this));
        for (var i = Job.Regions.Count - 1; i >= 0; i--)
        {
            if (RegionContains(Job.Regions[i], img))
            {
                if (ReferenceEquals(Job.Regions[i], _selectedRegion)) _selectedRegion = null;
                Job.Regions.RemoveAt(i);
                break;
            }
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var current = e.GetPosition(this);
        if (_activeHandle != HandleKind.None && _selectedRegion != null)
        {
            ApplyHandleDrag(_selectedRegion, ScreenToImage(current));
            InvalidatePreview();
            InvalidateVisual();
        }
        else if (_dragStartImage != null)
        {
            _dragEndImage = ScreenToImage(current);
            InvalidateVisual();
        }
        else if (_isPanning)
        {
            var dx = (float)(current.X - _lastMouse.X);
            var dy = (float)(current.Y - _lastMouse.Y);
            _pan = new SKPoint(_pan.X + dx, _pan.Y + dy);
            InvalidateVisual();
        }
        _lastMouse = current;
    }

    private void OnMouseAnyDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            _isPanning = true;
            _lastMouse = e.GetPosition(this);
            CaptureMouse();
        }
    }

    private void OnMouseAnyUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            _isPanning = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Bitmap == null) return;
        var factor = e.Delta > 0 ? 1.15f : 1f / 1.15f;
        var mp = e.GetPosition(this);
        var before = ScreenToImage(mp);
        _zoom *= factor;
        _zoom = Math.Clamp(_zoom, 0.05f, 20f);
        var after = ScreenToImage(mp);
        _pan = new SKPoint(
            _pan.X + (after.X - before.X) * _zoom,
            _pan.Y + (after.Y - before.Y) * _zoom);
        InvalidateVisual();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _selectedRegion != null && Job != null)
        {
            Job.Regions.Remove(_selectedRegion);
            _selectedRegion = null;
            InvalidatePreview();
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            FitToView();
            e.Handled = true;
        }
        else if (e.Key == Key.D1 || e.Key == Key.NumPad1)
        {
            ResetToOriginalSize();
            e.Handled = true;
        }
        else if (e.Key == Key.OemPlus || e.Key == Key.Add)
        {
            ZoomBy(1.25f);
            e.Handled = true;
        }
        else if (e.Key == Key.OemMinus || e.Key == Key.Subtract)
        {
            ZoomBy(1f / 1.25f);
            e.Handled = true;
        }
    }

    // ===== ハンドル判定/操作 =====

    private (HandleKind kind, int cornerIndex) HitTestHandle(MosaicRegion r, SKPoint img)
    {
        var tol = 12f / _zoom; // クリック許容
        if (r.Shape != RegionShape.OrientedRectangle) return (HandleKind.None, -1);

        // 回転ハンドル
        var rot = GetRotationHandlePosition(r);
        if (Distance(rot, img) <= tol) return (HandleKind.Rotate, -1);
        // 4 隅
        for (var i = 0; i < 4 && i < r.Points.Count; i++)
        {
            if (Distance(r.Points[i], img) <= tol)
                return (HandleKind.Resize, i);
        }
        // 中央 (移動) は領域内クリック全体で扱うのでここでは判定しない
        return (HandleKind.None, -1);
    }

    private static float Distance(SKPoint a, SKPoint b)
    {
        var dx = a.X - b.X; var dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private void ApplyHandleDrag(MosaicRegion r, SKPoint cursorImg)
    {
        if (r.Shape != RegionShape.OrientedRectangle) return;
        var dx = cursorImg.X - _grabAnchor.X;
        var dy = cursorImg.Y - _grabAnchor.Y;

        switch (_activeHandle)
        {
            case HandleKind.Move:
                r.Center = new SKPoint(_grabInitCenter.X + dx, _grabInitCenter.Y + dy);
                break;

            case HandleKind.Rotate:
            {
                // 中心からの相対角度差を grabAnchor 基準で計算
                var a0 = MathF.Atan2(_grabAnchor.Y - _grabInitCenter.Y, _grabAnchor.X - _grabInitCenter.X);
                var a1 = MathF.Atan2(cursorImg.Y - _grabInitCenter.Y, cursorImg.X - _grabInitCenter.X);
                r.AngleRad = _grabInitAngle + (a1 - a0);
                break;
            }

            case HandleKind.Resize:
            {
                // 対角の固定点を取り、cursor からの距離・方向で新サイズを決める
                // ローカル座標系で計算
                var cos = MathF.Cos(_grabInitAngle);
                var sin = MathF.Sin(_grabInitAngle);

                // cursor をローカル座標へ
                var cx = cursorImg.X - _grabInitCenter.X;
                var cy = cursorImg.Y - _grabInitCenter.Y;
                var lx = cx * cos + cy * sin;
                var ly = -cx * sin + cy * cos;

                // 対角コーナーのローカル座標（grabInitWidth/Heightの半分の符号付き）
                int signX, signY;
                switch (_activeCornerIndex)
                {
                    case 0: signX = -1; signY = -1; break; // LT
                    case 1: signX = +1; signY = -1; break; // RT
                    case 2: signX = +1; signY = +1; break; // RB
                    case 3: signX = -1; signY = +1; break; // LB
                    default: return;
                }
                var oppLX = -signX * _grabInitWidth / 2f;
                var oppLY = -signY * _grabInitHeight / 2f;
                var newLX = lx;
                var newLY = ly;

                var newW = MathF.Abs(newLX - oppLX);
                var newH = MathF.Abs(newLY - oppLY);
                if (newW < 4) newW = 4;
                if (newH < 4) newH = 4;

                // 新しい中心 (ローカル) = (oppLX + newLX)/2 を世界座標に戻す
                var midLX = (oppLX + newLX) / 2f;
                var midLY = (oppLY + newLY) / 2f;
                var worldX = _grabInitCenter.X + midLX * cos - midLY * sin;
                var worldY = _grabInitCenter.Y + midLX * sin + midLY * cos;

                r.Center = new SKPoint(worldX, worldY);
                r.Width = newW;
                r.Height = newH;
                break;
            }
        }

        r.RecomputeFromOrientedRect();
    }

    // ===== 領域ヒットテスト =====

    private static bool RegionContains(MosaicRegion r, SKPoint p)
    {
        return r.Shape switch
        {
            RegionShape.Rectangle => r.Bounds.Contains(p),
            RegionShape.Ellipse => EllipseContains(r.Bounds, p),
            RegionShape.Polygon or RegionShape.OrientedRectangle => PolygonContains(r.Points, p),
            _ => false,
        };
    }

    private static bool EllipseContains(SKRect r, SKPoint p)
    {
        var cx = r.MidX;
        var cy = r.MidY;
        var rx = r.Width / 2f;
        var ry = r.Height / 2f;
        if (rx <= 0 || ry <= 0) return false;
        var dx = (p.X - cx) / rx;
        var dy = (p.Y - cy) / ry;
        return dx * dx + dy * dy <= 1f;
    }

    private static bool PolygonContains(IReadOnlyList<SKPoint> poly, SKPoint p)
    {
        if (poly.Count < 3) return false;
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var pi = poly[i];
            var pj = poly[j];
            if (((pi.Y > p.Y) != (pj.Y > p.Y)) &&
                (p.X < (pj.X - pi.X) * (p.Y - pi.Y) / (pj.Y - pi.Y) + pi.X))
                inside = !inside;
        }
        return inside;
    }

    private static SKRect NormalizeRect(SKPoint a, SKPoint b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X, b.X);
        var bottom = Math.Max(a.Y, b.Y);
        return new SKRect(left, top, right, bottom);
    }
}
