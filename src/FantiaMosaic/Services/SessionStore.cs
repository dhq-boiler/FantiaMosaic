using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FantiaMosaic.Models;
using SkiaSharp;

namespace FantiaMosaic.Services;

/// <summary>
/// 編集中のジョブ一覧（領域情報含む）をJSONで保存/復元する。
/// 大量画像へのマスキング作業を中断・再開できるようにする目的。
/// </summary>
public sealed class SessionStore
{
    public sealed class SessionDto
    {
        public List<JobDto> Jobs { get; set; } = new();
        public SettingsDto Settings { get; set; } = new();
        public string OutputFolder { get; set; } = string.Empty;
    }

    public sealed class JobDto
    {
        public string SourcePath { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public string Suffix { get; set; } = "_mosaic";
        public List<RegionDto> Regions { get; set; } = new();
    }

    public sealed class RegionDto
    {
        public string Shape { get; set; } = "Rectangle";
        public float Left { get; set; }
        public float Top { get; set; }
        public float Right { get; set; }
        public float Bottom { get; set; }
        public List<float> PolygonXY { get; set; } = new();

        // OrientedRectangle 用フィールド (他形状時は使われない)
        public float CenterX { get; set; }
        public float CenterY { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float AngleRad { get; set; }

        // SolidFill モード用の領域別色 (0=未設定)
        public uint FillColorArgb { get; set; }
    }

    public sealed class SettingsDto
    {
        public string Mode { get; set; } = nameof(MosaicMode.PixelateThenBlur);
        public double RelativeStrength { get; set; } = 0.03;
        public int MinimumStrengthPx { get; set; } = 16;
        public double PostBlurRatio { get; set; } = 0.25;
        public int ThumbnailLongSide { get; set; }
        public uint SolidColorArgb { get; set; } = 0xFF000000;
    }

    public void Save(string path, IEnumerable<ImageJob> jobs, MosaicSettings settings, string outputFolder)
    {
        var dto = new SessionDto
        {
            OutputFolder = outputFolder,
            Settings = new SettingsDto
            {
                Mode = settings.Mode.ToString(),
                RelativeStrength = settings.RelativeStrength,
                MinimumStrengthPx = settings.MinimumStrengthPx,
                PostBlurRatio = settings.PostBlurRatio,
                ThumbnailLongSide = settings.ThumbnailLongSide,
                SolidColorArgb = (uint)settings.SolidColor,
            },
            Jobs = jobs.Select(SerializeJob).ToList(),
        };
        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public (List<ImageJob> Jobs, MosaicSettings Settings, string OutputFolder) Load(string path)
    {
        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<SessionDto>(json)
            ?? throw new InvalidDataException("セッションファイルが壊れてるっす");

        var settings = new MosaicSettings
        {
            Mode = System.Enum.TryParse<MosaicMode>(dto.Settings.Mode, out var m) ? m : MosaicMode.PixelateThenBlur,
            RelativeStrength = dto.Settings.RelativeStrength,
            MinimumStrengthPx = dto.Settings.MinimumStrengthPx,
            PostBlurRatio = dto.Settings.PostBlurRatio,
            ThumbnailLongSide = dto.Settings.ThumbnailLongSide,
            SolidColor = new SKColor(dto.Settings.SolidColorArgb),
        };

        var jobs = dto.Jobs.Select(DeserializeJob).ToList();
        return (jobs, settings, dto.OutputFolder);
    }

    private static JobDto SerializeJob(ImageJob j) => new()
    {
        SourcePath = j.SourcePath,
        Width = j.OriginalWidth,
        Height = j.OriginalHeight,
        Suffix = j.OutputSuffix,
        Regions = j.Regions.Select(SerializeRegion).ToList(),
    };

    private static ImageJob DeserializeJob(JobDto d)
    {
        var job = new ImageJob
        {
            SourcePath = d.SourcePath,
            OriginalWidth = d.Width,
            OriginalHeight = d.Height,
            OutputSuffix = d.Suffix,
        };
        foreach (var r in d.Regions)
            job.Regions.Add(DeserializeRegion(r));
        return job;
    }

    private static RegionDto SerializeRegion(MosaicRegion r)
    {
        var dto = new RegionDto
        {
            Shape = r.Shape.ToString(),
            Left = r.Bounds.Left,
            Top = r.Bounds.Top,
            Right = r.Bounds.Right,
            Bottom = r.Bounds.Bottom,
        };
        switch (r.Shape)
        {
            case RegionShape.Polygon:
                foreach (var p in r.Points)
                {
                    dto.PolygonXY.Add(p.X);
                    dto.PolygonXY.Add(p.Y);
                }
                break;
            case RegionShape.OrientedRectangle:
                dto.CenterX = r.Center.X;
                dto.CenterY = r.Center.Y;
                dto.Width = r.Width;
                dto.Height = r.Height;
                dto.AngleRad = r.AngleRad;
                break;
        }
        if (r.FillColor.HasValue)
            dto.FillColorArgb = (uint)r.FillColor.Value;
        return dto;
    }

    private static MosaicRegion DeserializeRegion(RegionDto d)
    {
        var rect = new SKRect(d.Left, d.Top, d.Right, d.Bottom);
        var region = d.Shape switch
        {
            nameof(RegionShape.Ellipse) => MosaicRegion.FromEllipse(rect),
            nameof(RegionShape.Polygon) => MosaicRegion.FromPolygon(PointsFromFlat(d.PolygonXY)),
            nameof(RegionShape.OrientedRectangle) => MosaicRegion.FromOrientedRect(
                new SKPoint(d.CenterX, d.CenterY), d.Width, d.Height, d.AngleRad),
            _ => MosaicRegion.FromRect(rect),
        };
        if (d.FillColorArgb != 0)
            region.FillColor = new SKColor(d.FillColorArgb);
        return region;
    }

    private static List<SKPoint> PointsFromFlat(List<float> xy)
    {
        var list = new List<SKPoint>(xy.Count / 2);
        for (var i = 0; i + 1 < xy.Count; i += 2)
            list.Add(new SKPoint(xy[i], xy[i + 1]));
        return list;
    }
}
