using System.Collections.ObjectModel;
using MangaDexLite.Core;
using MangaDexLite.Core.Studio;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MangaDexLite.Studio;

public sealed partial class StudioWindow : Window
{
    private readonly StudioOperations operations = new();
    private readonly WindowsImageProcessor processor = new();
    private readonly ITranslationProvider translator = new MockTranslationProvider();
    private readonly ObservableCollection<TextRegion> regionItems = [];
    private readonly Dictionary<Guid, Border> overlays = [];
    private StudioProject? project;
    private Guid? pageId, regionId;
    private string? projectFile;
    private bool canOverwrite = true, dirty, draftDirty, suppress, busy, closed, closeApproved, askingClose;
    private CancellationTokenSource? operationCancellation;
    private enum Gesture { None, Draw, Move, Resize }
    private Gesture gesture;
    private Point pointerStart;
    private PixelBounds? initialBounds, previewBounds;
    private Border? drawPreview;
    private uint pointerId;
    private StudioPage? CurrentPage => project is not null && pageId is Guid id ? StudioOperations.GetPage(project, id) : null;
    private TextRegion? CurrentRegion => CurrentPage is StudioPage page && regionId is Guid id ? StudioOperations.GetRegion(page, id) : null;

    public static string DemoProjectFile => Path.Combine(AppContext.BaseDirectory, "Assets", "StudioDemo", "demo.mangalite.json");
    private readonly string? initialProjectFile;

    public StudioWindow(string? initialProjectFile = null)
    {
        this.initialProjectFile = initialProjectFile;
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 900));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1120;
            presenter.PreferredMinimumHeight = 760;
        }
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RegionsList.ItemsSource = regionItems;
        AppWindow.Closing += Window_Closing;
        Closed += Window_Closed;
        if (initialProjectFile is not null) Root.Loaded += InitialProject_Loaded;
    }

    private async void InitialProject_Loaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= InitialProject_Loaded;
        await OpenProjectAsync(initialProjectFile!);
    }

    private Task<bool> OpenProjectAsync(string file) => RunAsync(async ct =>
    {
        var loaded = await operations.LoadAsync(file, ct);
        bool demo = string.Equals(Path.GetFullPath(file), DemoProjectFile, StringComparison.OrdinalIgnoreCase);
        await InstallProjectAsync(loaded.Project, demo ? null : file, loaded.CanOverwrite, ct);
        if (demo) SetDirty(true);
        if (loaded.Warnings.Count > 0)
        {
            MessageBar.Severity = InfoBarSeverity.Warning;
            MessageBar.Message = string.Join("\n", loaded.Warnings.Take(5)) + "\nИсходный JSON сохранён. Используйте новое имя файла.";
            MessageBar.IsOpen = true;
        }
    });

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !await ConfirmReplacementAsync()) return;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || closed) return;
            await RunAsync(async ct =>
            {
                var imported = await operations.ImportChapterAsync(folder.Path, processor, ct);
                await InstallProjectAsync(imported, null, true, ct);
                SetDirty(true);
                Status.Text = $"Импортировано страниц: {imported.Pages.Count}. Нарисуйте область, затем введите исходный текст.";
            });
        }
        catch (Exception ex) { if (!closed) ShowError(ex.Message); }
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !await ConfirmReplacementAsync()) return;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".json");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is null || closed) return;
            if (await OpenProjectAsync(file.Path)) Status.Text = "Проект открыт. Выберите страницу или область.";
        }
        catch (Exception ex) { if (!closed) ShowError(ex.Message); }
    }

    private async void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !await ConfirmReplacementAsync()) return;
        if (await OpenProjectAsync(DemoProjectFile))
            Status.Text = "Демо: выберите область «Hello!», измените перевод и сохраните проект. Изображение останется неизменным.";
    }

    private async Task InstallProjectAsync(StudioProject candidate, string? file, bool overwrite, CancellationToken ct)
    {
        var first = candidate.Pages[0];
        // Stage image decoding too: a failed import/open must retain the old project and drafts.
        var image = await processor.ReadPreviewAsync(Path.Combine(candidate.SourceDirectory, first.SourceFile), first, ct);
        ct.ThrowIfCancellationRequested();
        project = candidate;
        projectFile = file;
        canOverwrite = overwrite;
        pageId = first.Id;
        regionId = null;
        draftDirty = false;
        suppress = true;
        try
        {
            PagesList.ItemsSource = candidate.Pages;
            PagesList.SelectedItem = first;
            TargetLanguage.Text = candidate.TargetLanguage;
        }
        finally { suppress = false; }
        ShowPage(first, image);
        SetDirty(false);
    }

    private void ShowPage(StudioPage page, Microsoft.UI.Xaml.Media.ImageSource image)
    {
        PageImage.Source = image;
        PageSurface.Width = RegionCanvas.Width = page.PixelWidth;
        PageSurface.Height = RegionCanvas.Height = page.PixelHeight;
        PageSurface.Visibility = Visibility.Visible;
        EmptyHint.Visibility = Visibility.Collapsed;
        regionId = null;
        ReloadRegions();
        if (page.Regions.Count > 0) SelectRegion(page.Regions[0].Id);
        FitPage();
    }

    private async void Page_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (suppress || busy || PagesList.SelectedItem is not StudioPage next || next.Id == pageId || project is null) return;
        if (!ApplyDraft()) { RestorePageSelection(); return; }
        var previousId = pageId;
        bool succeeded = await RunAsync(async ct =>
        {
            var actual = StudioOperations.GetPage(project, next.Id);
            var image = await processor.ReadPreviewAsync(Path.Combine(project.SourceDirectory, actual.SourceFile), actual, ct);
            pageId = actual.Id;
            ShowPage(actual, image);
            Status.Text = $"{actual.SourceFile} · {actual.PixelWidth} × {actual.PixelHeight} px · {actual.Regions.Count} областей";
        });
        if (!succeeded && !closed) { pageId = previousId; RestorePageSelection(); }
    }

    private void RestorePageSelection()
    {
        suppress = true;
        try { PagesList.SelectedItem = PagesList.Items.OfType<StudioPage>().FirstOrDefault(p => p.Id == pageId); }
        finally { suppress = false; }
    }

    private void ReloadRegions()
    {
        suppress = true;
        try
        {
            regionItems.Clear();
            RegionCanvas.Children.Clear();
            overlays.Clear();
            foreach (var region in CurrentPage?.Regions ?? []) { regionItems.Add(region); UpdateOverlay(region); }
            RegionsList.SelectedItem = regionItems.FirstOrDefault(r => r.Id == regionId);
        }
        finally { suppress = false; }
        LoadEditor();
    }

    private void Region_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (suppress || busy) return;
        Guid? next = (RegionsList.SelectedItem as TextRegion)?.Id;
        if (!ApplyDraft())
        {
            suppress = true;
            try { RegionsList.SelectedItem = regionItems.FirstOrDefault(r => r.Id == regionId); }
            finally { suppress = false; }
            return;
        }
        SelectRegion(next);
    }

    private void SelectRegion(Guid? id)
    {
        Guid? previous = regionId;
        regionId = id;
        suppress = true;
        try { RegionsList.SelectedItem = regionItems.FirstOrDefault(r => r.Id == id); }
        finally { suppress = false; }
        if (CurrentPage is StudioPage page)
        {
            if (previous is Guid old && page.Regions.FirstOrDefault(r => r.Id == old) is TextRegion prior) UpdateOverlay(prior);
            if (CurrentRegion is TextRegion current) UpdateOverlay(current);
        }
        LoadEditor();
    }

    private void LoadEditor()
    {
        suppress = true;
        try
        {
            var region = CurrentRegion;
            Editor.IsEnabled = region is not null && !busy;
            SourceEditor.Text = region?.SourceText ?? "";
            TranslationEditor.Text = region?.Translation ?? "";
            XBox.Value = region?.Bounds.X ?? 0;
            YBox.Value = region?.Bounds.Y ?? 0;
            WidthBox.Value = region?.Bounds.Width ?? 1;
            HeightBox.Value = region?.Bounds.Height ?? 1;
            ReviewBox.SelectedIndex = region is null ? 0 : (int)region.ReviewStatus;
            RegionHint.Text = region is null ? "Нарисуйте прямоугольник или выберите область." : $"Область {region.Id.ToString()[..8]} · {region.TranslationProvider ?? "вручную"}";
            draftDirty = false;
        }
        finally { suppress = false; }
    }

    private void Draft_Changed(object sender, TextChangedEventArgs e) => MarkDraft();
    private void Coordinate_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e) => MarkDraft();
    private void Review_Changed(object sender, SelectionChangedEventArgs e) => MarkDraft();
    private void MarkDraft()
    {
        if (suppress || project is null || busy) return;
        var region = CurrentRegion;
        draftDirty = TargetLanguage.Text.Trim() != project.TargetLanguage || region is not null &&
            (ReadEditorBounds(region.Bounds) != region.Bounds || SourceEditor.Text != region.SourceText || TranslationEditor.Text != region.Translation || ReviewBox.SelectedIndex != (int)region.ReviewStatus);
        if (draftDirty) SetDirty(true);
    }

    private PixelBounds ReadEditorBounds(PixelBounds original)
    {
        // NumberBox formatting can round-trip through a different double representation.
        // Preserve the model's exact pixels unless a coordinate was meaningfully edited.
        static double Value(double editor, double model) => Math.Abs(editor - model) < 0.000001 ? model : editor;
        return new(Value(XBox.Value, original.X), Value(YBox.Value, original.Y), Value(WidthBox.Value, original.Width), Value(HeightBox.Value, original.Height));
    }

    private bool ApplyDraft()
    {
        if (!draftDirty || project is null) return true;
        try
        {
            if (string.IsNullOrWhiteSpace(TargetLanguage.Text)) throw new ArgumentException("Укажите язык результата, например ru или en.");
            var updated = project with { TargetLanguage = TargetLanguage.Text.Trim() };
            if (CurrentRegion is TextRegion region && pageId is Guid p)
            {
                var bounds = ReadEditorBounds(region.Bounds);
                bool contentChanged = region.Bounds != bounds || region.SourceText != SourceEditor.Text || region.Translation != TranslationEditor.Text;
                updated = operations.UpdateRegion(updated, p, region.Id, bounds, SourceEditor.Text, TranslationEditor.Text);
                // Editing an approved region invalidates approval until the user explicitly reviews again.
                ReviewStatus status = contentChanged ? ReviewStatus.Draft : (ReviewStatus)Math.Max(0, ReviewBox.SelectedIndex);
                updated = operations.SetReviewStatus(updated, p, region.Id, status);
            }
            project = updated;
            if (CurrentRegion is TextRegion current) RefreshRegion(current);
            LoadEditor();
            draftDirty = false;
            return true;
        }
        catch (Exception ex) { ShowError(ex.Message); return false; }
    }

    private void RefreshRegion(TextRegion region)
    {
        suppress = true;
        try
        {
            int index = -1;
            for (int i = 0; i < regionItems.Count; i++) if (regionItems[i].Id == region.Id) { index = i; break; }
            if (index >= 0) regionItems[index] = region;
            RegionsList.SelectedItem = regionItems.FirstOrDefault(r => r.Id == regionId);
        }
        finally { suppress = false; }
        UpdateOverlay(region);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ApplyDraft()) Status.Text = "Изменения области применены. Сохраните проект, чтобы записать их на диск.";
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !ApplyDraft() || project is null || pageId is not Guid p || regionId is not Guid r) return;
        await RunAsync(async ct =>
        {
            project = await operations.TranslateRegionAsync(project, p, r, translator, ct);
            RefreshRegion(CurrentRegion!);
            LoadEditor();
            SetDirty(true);
            Status.Text = "Mock создал демонстрационный результат. Отредактируйте его и проверьте перевод.";
        });
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !ApplyDraft() || project is null || pageId is not Guid p || regionId is not Guid r) return;
        project = operations.RemoveRegion(project, p, r);
        regionId = null;
        draftDirty = false;
        ReloadRegions();
        SetDirty(true);
    }

    private void UpdateOverlay(TextRegion region, PixelBounds? preview = null)
    {
        if (!overlays.TryGetValue(region.Id, out var border))
        {
            border = new Border { BorderThickness = new Thickness(2), IsHitTestVisible = false, Padding = new Thickness(6) };
            overlays.Add(region.Id, border);
            RegionCanvas.Children.Add(border);
        }
        Position(border, preview ?? region.Bounds);
        border.BorderBrush = new SolidColorBrush(region.Id == regionId ? Colors.DarkOrange : Windows.UI.Color.FromArgb(255, 103, 70, 232));
        border.Background = new SolidColorBrush(PreviewCheck.IsChecked == true && region.Translation.Length > 0 ? Colors.White : Windows.UI.Color.FromArgb(30, 103, 70, 232));
        var content = border.Child as Grid;
        if (content is null)
        {
            content = new Grid();
            content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Colors.Black) });
            content.Children.Add(new Border { Width = 14, Height = 14, Background = new SolidColorBrush(Colors.DarkOrange), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom });
            border.Child = content;
        }
        var text = (TextBlock)content.Children[0];
        text.Text = PreviewCheck.IsChecked == true ? region.Translation : "";
        text.FontSize = Math.Clamp(region.Bounds.Width / 12, 14, 32);
        content.Children[1].Visibility = region.Id == regionId ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void Position(Border border, PixelBounds bounds)
    {
        Canvas.SetLeft(border, bounds.X);
        Canvas.SetTop(border, bounds.Y);
        border.Width = bounds.Width;
        border.Height = bounds.Height;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        foreach (var region in CurrentPage?.Regions ?? []) UpdateOverlay(region);
    }

    private void Canvas_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (busy || gesture != Gesture.None || !ApplyDraft() || CurrentPage is not StudioPage page) return;
        var point = e.GetCurrentPoint(RegionCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        pointerStart = Clamp(point.Position, page);
        var hit = page.Regions.Reverse().FirstOrDefault(r => r.Bounds.Contains(pointerStart.X, pointerStart.Y));
        if (DrawButton.IsChecked == true || hit is null)
        {
            gesture = Gesture.Draw;
            drawPreview = new Border { BorderBrush = new SolidColorBrush(Colors.DarkOrange), BorderThickness = new Thickness(2), IsHitTestVisible = false };
            RegionCanvas.Children.Add(drawPreview);
        }
        else
        {
            SelectRegion(hit.Id);
            initialBounds = hit.Bounds;
            double grip = 16 / PageScroll.ZoomFactor;
            gesture = pointerStart.X >= hit.Bounds.X + hit.Bounds.Width - grip && pointerStart.Y >= hit.Bounds.Y + hit.Bounds.Height - grip ? Gesture.Resize : Gesture.Move;
        }
        previewBounds = null;
        pointerId = e.Pointer.PointerId;
        RegionCanvas.CancelDirectManipulations();
        if (!RegionCanvas.CapturePointer(e.Pointer)) { CancelGesture(); return; }
        e.Handled = true;
    }

    private void Canvas_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (gesture == Gesture.None || pointerId != e.Pointer.PointerId || CurrentPage is not StudioPage page) return;
        var point = Clamp(e.GetCurrentPoint(RegionCanvas).Position, page);
        previewBounds = gesture switch
        {
            Gesture.Draw => PixelBounds.FromPoints(pointerStart.X, pointerStart.Y, point.X, point.Y),
            Gesture.Move => initialBounds!.MoveTo(initialBounds.X + point.X - pointerStart.X, initialBounds.Y + point.Y - pointerStart.Y, page.PixelWidth, page.PixelHeight),
            _ => initialBounds!.ResizeTo(initialBounds.Width + point.X - pointerStart.X, initialBounds.Height + point.Y - pointerStart.Y, page.PixelWidth, page.PixelHeight)
        };
        if (gesture == Gesture.Draw) Position(drawPreview!, previewBounds);
        else UpdateOverlay(CurrentRegion!, previewBounds);
        e.Handled = true;
    }

    private void Canvas_Released(object sender, PointerRoutedEventArgs e)
    {
        if (gesture == Gesture.None || pointerId != e.Pointer.PointerId || project is null || CurrentPage is not StudioPage page) return;
        try
        {
            if (previewBounds is PixelBounds bounds && bounds.Width >= 4 && bounds.Height >= 4)
            {
                if (gesture == Gesture.Draw)
                {
                    project = operations.AddRegion(project, page.Id, bounds);
                    regionId = CurrentPage!.Regions[^1].Id;
                    ReloadRegions();
                    DrawButton.IsChecked = false;
                }
                else if (CurrentRegion is TextRegion region && region.Bounds != bounds)
                {
                    project = operations.UpdateRegion(project, page.Id, region.Id, bounds, region.SourceText, region.Translation);
                    RefreshRegion(CurrentRegion!);
                    LoadEditor();
                }
                SetDirty(true);
            }
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            CancelGesture();
            RegionCanvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }
    }

    private void Canvas_Canceled(object sender, PointerRoutedEventArgs e)
    {
        if (gesture != Gesture.None && pointerId == e.Pointer.PointerId) CancelGesture();
    }
    private void CancelGesture()
    {
        if (drawPreview is not null) RegionCanvas.Children.Remove(drawPreview);
        drawPreview = null;
        gesture = Gesture.None;
        previewBounds = initialBounds = null;
        if (CurrentRegion is TextRegion region) UpdateOverlay(region);
    }
    private static Point Clamp(Point point, StudioPage page) => new(Math.Clamp(point.X, 0, page.PixelWidth), Math.Clamp(point.Y, 0, page.PixelHeight));
    private void Fit_Click(object sender, RoutedEventArgs e) => FitPage();
    private void ActualSize_Click(object sender, RoutedEventArgs e) => PageScroll.ChangeView(null, null, 1, true);
    private void FitPage()
    {
        if (CurrentPage is not StudioPage page) return;
        double width = Math.Max(100, PageScroll.ActualWidth - 20), height = Math.Max(100, PageScroll.ActualHeight - 20);
        PageScroll.ChangeView(0, 0, (float)Math.Clamp(Math.Min(width / page.PixelWidth, height / page.PixelHeight), PageScroll.MinZoomFactor, 1), true);
    }
    private void View_Changed(object sender, ScrollViewerViewChangedEventArgs e) => ZoomLabel.Text = $"{PageScroll.ZoomFactor * 100:0}%";

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveProjectAsync(false);
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveProjectAsync(true);
    private async Task<bool> SaveProjectAsync(bool saveAs)
    {
        if (busy || project is null || !ApplyDraft()) return false;
        string? path = projectFile;
        try
        {
            if (saveAs || path is null || !canOverwrite)
            {
                // Unlike the legacy StorageFile picker, this returns a path without creating/truncating it.
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id) { SuggestedFileName = OutputNames.Safe(project.Title) + ".mangalite" };
                picker.FileTypeChoices.Add("MangaLite Studio project", new[] { ".json" });
                var file = await picker.PickSaveFileAsync();
                if (file is null || closed) return false;
                path = file.Path;
                if (!path.EndsWith(".mangalite.json", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Имя проекта должно оканчиваться на .mangalite.json.");
            }
            return await RunAsync(async ct =>
            {
                await operations.SaveAsync(project, path, ct);
                projectFile = path;
                canOverwrite = true;
                SetDirty(false);
                Status.Text = "Проект сохранён: " + path;
            });
        }
        catch (Exception ex) { if (!closed) ShowError(ex.Message); return false; }
    }

    private async Task<bool> ConfirmReplacementAsync()
    {
        if (!dirty) return true;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "Сохранить изменения?", Content = "В проекте есть несохранённые правки.", PrimaryButtonText = "Сохранить", SecondaryButtonText = "Не сохранять", CloseButtonText = "Отмена", DefaultButton = ContentDialogButton.Primary };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Secondary || (result == ContentDialogResult.Primary && await SaveProjectAsync(false));
    }

    private async Task<bool> RunAsync(Func<CancellationToken, Task> action)
    {
        if (busy || closed) return false;
        using var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        SetBusy(true);
        MessageBar.IsOpen = false;
        try { await action(cancellation.Token); return true; }
        catch (OperationCanceledException) { if (!closed) Status.Text = "Операция отменена. Текущий проект сохранён в рабочем пространстве."; return false; }
        catch (Exception ex) { if (!closed) ShowError(ex.Message); return false; }
        finally
        {
            operationCancellation = null;
            if (!closed) SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        BusyBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = OpenButton.IsEnabled = DemoButton.IsEnabled = PagesList.IsEnabled = RegionsList.IsEnabled = !value;
        SaveButton.IsEnabled = SaveAsButton.IsEnabled = DrawButton.IsEnabled = project is not null && !value;
        Editor.IsEnabled = CurrentRegion is not null && !value;
    }
    private void SetDirty(bool value)
    {
        dirty = value;
        ProjectTitle.Text = (project?.Title ?? "Translation Studio") + (value ? " · не сохранено" : "");
    }
    private void ShowError(string message)
    {
        MessageBar.Message = message;
        MessageBar.Severity = InfoBarSeverity.Error;
        MessageBar.IsOpen = true;
    }
    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (closeApproved || !dirty && !busy) return;
        args.Cancel = true;
        if (busy) { operationCancellation?.Cancel(); return; }
        if (askingClose) return;
        askingClose = true;
        try
        {
            if (await ConfirmReplacementAsync()) { closeApproved = true; Close(); }
        }
        finally { askingClose = false; }
    }
    private void Window_Closed(object sender, WindowEventArgs args)
    {
        closed = true;
        operationCancellation?.Cancel();
        AppWindow.Closing -= Window_Closing;
        PageImage.Source = null;
        overlays.Clear();
    }
}
