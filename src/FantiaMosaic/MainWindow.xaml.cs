using System.Windows;
using System.Windows.Input;
using FantiaMosaic.ViewModels;

namespace FantiaMosaic;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        InputBindings.Add(new KeyBinding(
            new RelayDelegateCommand(() => ViewModel.OpenFilesCommand.Execute(null)),
            new KeyGesture(Key.O, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new RelayDelegateCommand(() => ViewModel.OpenFolderCommand.Execute(null)),
            new KeyGesture(Key.O, ModifierKeys.Control | ModifierKeys.Shift)));
        InputBindings.Add(new KeyBinding(
            new RelayDelegateCommand(() => ViewModel.ExportAllCommand.Execute(null)),
            new KeyGesture(Key.E, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new RelayDelegateCommand(() => ViewModel.SaveSessionCommand.Execute(null)),
            new KeyGesture(Key.S, ModifierKeys.Control)));
        // ビューポート操作 (Ctrl+0 でフィット、Ctrl+1 で等倍 — 一般的なお作法)
        InputBindings.Add(new KeyBinding(
            new RelayDelegateCommand(() => Editor.FitToView()),
            new KeyGesture(Key.D0, ModifierKeys.Control)));
        InputBindings.Add(new KeyBinding(
            new RelayDelegateCommand(() => Editor.ResetToOriginalSize()),
            new KeyGesture(Key.D1, ModifierKeys.Control)));
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            ViewModel.AddFilesCommand.Execute(files);
    }

    private void OnFitClick(object sender, RoutedEventArgs e) => Editor.FitToView();
    private void OnResetSizeClick(object sender, RoutedEventArgs e) => Editor.ResetToOriginalSize();
    private void OnZoomInClick(object sender, RoutedEventArgs e) => Editor.ZoomBy(1.25f);
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => Editor.ZoomBy(1f / 1.25f);
}

internal sealed class RelayDelegateCommand : ICommand
{
    private readonly System.Action _action;
    public RelayDelegateCommand(System.Action action) => _action = action;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _action();
    public event System.EventHandler? CanExecuteChanged;
}
