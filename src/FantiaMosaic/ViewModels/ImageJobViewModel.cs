using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using FantiaMosaic.Models;

namespace FantiaMosaic.ViewModels;

public sealed partial class ImageJobViewModel : ObservableObject
{
    public ImageJob Job { get; }

    [ObservableProperty]
    private string fileName = string.Empty;

    [ObservableProperty]
    private string sizeText = string.Empty;

    [ObservableProperty]
    private int regionCount;

    public ObservableCollection<MosaicRegion> Regions => Job.Regions;

    public ImageJobViewModel(ImageJob job)
    {
        Job = job;
        FileName = Path.GetFileName(job.SourcePath);
        SizeText = $"{job.OriginalWidth}×{job.OriginalHeight}";
        Regions.CollectionChanged += (_, _) => RegionCount = Regions.Count;
        RegionCount = Regions.Count;
    }
}
