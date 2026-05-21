using System;
using FantiaMosaic.Models;
using SkiaSharp;
using Xunit;

namespace FantiaMosaic.Tests;

public class MosaicRegionTests
{
    [Fact]
    public void FromPolygon_RequiresAtLeastThreePoints()
    {
        Assert.Throws<ArgumentException>(() =>
            MosaicRegion.FromPolygon(new[] { new SKPoint(0, 0), new SKPoint(10, 0) }));
    }

    [Fact]
    public void FromPolygon_ComputesBoundsFromVertices()
    {
        var pts = new[] { new SKPoint(10, 20), new SKPoint(50, 5), new SKPoint(30, 60) };
        var r = MosaicRegion.FromPolygon(pts);
        Assert.Equal(new SKRect(10, 5, 50, 60), r.Bounds);
    }

    [Fact]
    public void FromRect_PreservesBoundsAndShape()
    {
        var r = MosaicRegion.FromRect(new SKRect(1, 2, 3, 4));
        Assert.Equal(RegionShape.Rectangle, r.Shape);
        Assert.Equal(new SKRect(1, 2, 3, 4), r.Bounds);
    }
}
