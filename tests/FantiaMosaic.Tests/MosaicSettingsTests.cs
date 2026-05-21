using FantiaMosaic.Models;
using Xunit;

namespace FantiaMosaic.Tests;

public class MosaicSettingsTests
{
    [Fact]
    public void ResolveStrengthPx_ReturnsRatioOfShortSide_WhenAboveMinimum()
    {
        var s = new MosaicSettings { RelativeStrength = 0.05, MinimumStrengthPx = 8 };
        // 短辺 1000 × 5% = 50 px (≥ 下限 8)
        Assert.Equal(50, s.ResolveStrengthPx(2000, 1000));
    }

    [Fact]
    public void ResolveStrengthPx_ReturnsMinimum_WhenRatioWouldBeTooSmall()
    {
        var s = new MosaicSettings { RelativeStrength = 0.01, MinimumStrengthPx = 16 };
        // 短辺 300 × 1% = 3 px → 下限の 16 にクランプ
        Assert.Equal(16, s.ResolveStrengthPx(800, 300));
    }

    [Fact]
    public void DefaultPreset_HasReasonableValues()
    {
        var s = MosaicSettings.DefaultPreset();
        Assert.Equal(MosaicMode.PixelateThenBlur, s.Mode);
        Assert.True(s.RelativeStrength >= 0.02, "ガイド準拠の下限ライン");
        Assert.True(s.MinimumStrengthPx >= 16);
    }

    [Fact]
    public void StrongPreset_IsStrongerThanDefault()
    {
        var d = MosaicSettings.DefaultPreset();
        var s = MosaicSettings.StrongPreset();
        // 動画/高解像度向けプリセットは静止画向けより必ず強く設定されているはず
        Assert.True(s.RelativeStrength > d.RelativeStrength);
        Assert.True(s.MinimumStrengthPx >= d.MinimumStrengthPx);
    }
}
