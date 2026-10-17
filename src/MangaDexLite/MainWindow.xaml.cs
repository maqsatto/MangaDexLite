using System.Collections.ObjectModel;
using System.Globalization;
using MangaDexLite.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace MangaDexLite;

public sealed partial class MainWindow : Window
{
    private readonly MangaDexClient api = new();
    private readonly ObservableCollection<Manga> results = [];
    private readonly SettingsStore settings = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MangaDexLite", "settings.json"));
    private CancellationTokenSource? searchCancellation;
    private CancellationTokenSource? feedCancellation;
    private CancellationTokenSource? downloadCancellation;
    private IReadOnlyList<Chapter> chapters = [];
    private Manga? currentManga;
    private bool initializing = true;
    private bool downloading;
    private bool closed;
    private bool loadingFeed;
    private bool updatingSelection;
    private string searchQuery = "";
    private int searchOffset;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1160, 850));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1000;
            presenter.PreferredMinimumHeight = 720;
        }
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ResultsList.ItemsSource = results;
        LanguageBox.ItemsSource = Languages(null);
        LanguageBox.SelectedValue = "ru";
        GroupBox.ItemsSource = new[] { new Group("", "Все группы") };
        GroupBox.SelectedIndex = 0;
        Root.Loaded += Root_Loaded;
        Closed += Window_Closed;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= Root_Loaded;
        var loaded = await settings.LoadAsync();
        if (closed) return;
        FolderBox.Text = loaded.Settings.Directory.Length > 0 ? loaded.Settings.Directory : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MangaDex");
        CbzCheck.IsChecked = loaded.Settings.Cbz;
        SaverCheck.IsChecked = loaded.Settings.DataSaver;
        LanguageBox.ItemsSource = Languages([loaded.Settings.Language]);
        LanguageBox.SelectedValue = loaded.Settings.Language;
        initializing = false;
        if (loaded.Warning is not null) ShowMessage(loaded.Warning, InfoBarSeverity.Warning);
    }

    private static IReadOnlyList<Group> Languages(IReadOnlyList<string>? extra)
    {
        var known = new Dictionary<string, string>
        {
            [""] = "Все языки", ["ru"] = "Русский", ["en"] = "English", ["uk"] = "Українська", ["kk"] = "Қазақша",
            ["ja"] = "日本語", ["ko"] = "한국어", ["zh"] = "中文", ["zh-hk"] = "繁體中文", ["es"] = "Español",
            ["es-la"] = "Español (LatAm)", ["pt-br"] = "Português (Brasil)", ["fr"] = "Français", ["de"] = "Deutsch",
            ["it"] = "Italiano", ["pl"] = "Polski", ["tr"] = "Türkçe", ["id"] = "Indonesia", ["vi"] = "Tiếng Việt", ["ar"] = "العربية"
        };
        foreach (string language in extra ?? []) known.TryAdd(language, language.ToUpperInvariant());
        return known.Select(k => new Group(k.Key, k.Value)).ToList();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(false);
    private async void More_Click(object sender, RoutedEventArgs e) => await SearchAsync(true);
    private async void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; await SearchAsync(false); }
    }

    private async Task SearchAsync(bool more)
    {
        if (downloading || initializing) return;
        string query = more ? searchQuery : SearchBox.Text.Trim();
        if (query.Length == 0) { SearchBox.Focus(FocusState.Programmatic); return; }
        searchCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        searchCancellation = cancellation;
        MoreButton.IsEnabled = false;
        if (!more)
        {
            feedCancellation?.Cancel();
            currentManga = null;
            chapters = [];
            FilterChapters();
            results.Clear();
            MangaTitle.Text = "Результаты поиска";
            MangaDescription.Text = "Выберите тайтл в списке слева.";
            searchOffset = 0;
            searchQuery = query;
        }
        Status.Text = "Поиск на MangaDex…";
        MessageBar.IsOpen = false;
        try
        {
            var page = await api.SearchAsync(query, cancellation.Token, searchOffset);
            if (closed || cancellation.IsCancellationRequested) return;
            foreach (var manga in page) results.Add(manga);
            searchOffset += page.Count;
            MoreButton.IsEnabled = page.Count == 30 && searchOffset < 10000 && MangaDexClient.TryTitleId(query) is null;
            Status.Text = page.Count == 0 ? "Ничего не найдено. Попробуйте другое название или ссылку." : $"Найдено: {results.Count}. Выберите тайтл.";
            if (!more && results.Count == 1) ResultsList.SelectedIndex = 0;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && !cancellation.IsCancellationRequested) ShowMessage(ex.Message); }
        finally { if (ReferenceEquals(searchCancellation, cancellation)) searchCancellation = null; }
    }

    private async void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (downloading || ResultsList.SelectedItem is not Manga manga) return;
        currentManga = manga;
        MangaTitle.Text = manga.Title;
        MangaDescription.Text = manga.Description.Length == 0 ? "Описание отсутствует." : manga.Description;
        string language = LanguageBox.SelectedValue as string ?? "ru";
        initializing = true;
        LanguageBox.ItemsSource = Languages((manga.Languages ?? []).Append(language).ToList());
        LanguageBox.SelectedValue = language;
        initializing = false;
        await LoadChaptersAsync();
    }

    private async void Language_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing && !downloading && currentManga is not null) await LoadChaptersAsync();
    }

    private async Task LoadChaptersAsync()
    {
        if (currentManga is not Manga manga) return;
        feedCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        feedCancellation = cancellation;
        chapters = [];
        loadingFeed = true;
        FilterChapters();
        Status.Text = "Загрузка глав и групп перевода…";
        string language = LanguageBox.SelectedValue as string ?? "";
        try
        {
            var loaded = await api.GetChaptersAsync(manga.Id, language, cancellation.Token);
            if (closed || cancellation.IsCancellationRequested) return;
            chapters = loaded;
            initializing = true;
            var groups = loaded.SelectMany(c => c.Groups).DistinctBy(g => g.Id).OrderBy(g => g.Name).ToList();
            groups.Insert(0, new("", "Все группы"));
            if (loaded.Any(c => c.Groups.Count == 0)) groups.Add(new("ungrouped", "Без группы"));
            GroupBox.ItemsSource = groups;
            GroupBox.SelectedIndex = 0;
            initializing = false;
            loadingFeed = false;
            FilterChapters();
            Status.Text = $"Глав: {chapters.Count}. Выберите группу и отметьте нужные главы.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && !cancellation.IsCancellationRequested) ShowMessage(ex.Message); }
        finally
        {
            if (ReferenceEquals(feedCancellation, cancellation))
            {
                feedCancellation = null;
                loadingFeed = false;
                if (!closed) FilterChapters();
            }
        }
    }

    private void Group_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!initializing && !downloading) FilterChapters();
    }

    private void FilterChapters()
    {
        string? group = GroupBox.SelectedValue as string;
        var visible = chapters.Where(c => c.Matches(null, group)).ToList();
        ChaptersList.ItemsSource = visible;
        EmptyHint.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = loadingFeed ? "Загрузка глав…" : currentManga is null ? "Здесь появятся главы выбранного тайтла" : "На выбранном языке главы не найдены";
        UpdateSelection();
    }

    private void Chapters_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingSelection) UpdateSelection();
    }
    private void UpdateSelection()
    {
        int count = ChaptersList.SelectedItems.OfType<Chapter>().Count(c => c.CanDownload);
        SelectionCount.Text = $"Выбрано: {count}";
        DownloadButton.IsEnabled = count > 0 && !downloading && !loadingFeed;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (downloading) return;
        SelectMatching(static chapter => chapter.CanDownload);
    }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) { if (!downloading) ChaptersList.SelectedItems.Clear(); }
    private void Range_Click(object sender, RoutedEventArgs e)
    {
        if (downloading) return;
        if (!decimal.TryParse(RangeFrom.Text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var from) ||
            !decimal.TryParse(RangeTo.Text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var to) || from > to)
        { ShowMessage("Введите диапазон глав, например 1 — 12.5.", InfoBarSeverity.Warning); return; }
        SelectMatching(chapter => chapter.CanDownload && chapter.InRange(from, to));
    }

    private void SelectMatching(Func<Chapter, bool> predicate)
    {
        // Coalesce our count refresh: each individual selected item raises SelectionChanged.
        updatingSelection = true;
        try
        {
            ChaptersList.SelectedItems.Clear();
            foreach (Chapter chapter in ChaptersList.Items)
                if (predicate(chapter)) ChaptersList.SelectedItems.Add(chapter);
        }
        finally { updatingSelection = false; UpdateSelection(); }
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null && !closed) { FolderBox.Text = folder.Path; await SaveSettingsAsync(); }
        }
        catch (Exception ex) { if (!closed) ShowMessage(ex.Message); }
    }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(FolderBox.Text)) { ShowMessage("Папка появится после первого скачивания.", InfoBarSeverity.Informational); return; }
            await Launcher.LaunchFolderPathAsync(Path.GetFullPath(FolderBox.Text));
        }
        catch (Exception ex) { ShowMessage(ex.Message); }
    }
    private async void OpenSite_Click(object sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri("https://mangadex.org"));
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        downloadCancellation?.Cancel();
        CancelButton.IsEnabled = false;
        Status.Text = "Отмена… Уже скачанные страницы сохраняются.";
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (downloading || currentManga is not Manga manga) return;
        var selected = ChaptersList.SelectedItems.OfType<Chapter>().Where(c => c.CanDownload).ToHashSet();
        var queue = chapters.Where(selected.Contains).ToList();
        if (queue.Count == 0) return;
        string directory = FolderBox.Text.Trim();
        if (!Path.IsPathFullyQualified(directory)) { ShowMessage("Выберите полный путь к папке скачивания."); return; }
        var options = new DownloadOptions(directory, CbzCheck.IsChecked == true, SaverCheck.IsChecked == true);
        using var cancellation = new CancellationTokenSource();
        downloadCancellation = cancellation;
        searchCancellation?.Cancel();
        feedCancellation?.Cancel();
        SetDownloading(true);
        await SaveSettingsAsync();
        MessageBar.IsOpen = false;
        int completed = 0;
        var errors = new List<string>();
        try
        {
            var downloader = new ChapterDownloader(api);
            for (int i = 0; i < queue.Count; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                int position = i;
                var chapter = queue[i];
                var progress = new Progress<DownloadProgress>(p =>
                {
                    if (closed || cancellation.IsCancellationRequested) return;
                    Progress.Value = 100.0 * (position + (double)p.CompletedPages / p.TotalPages) / queue.Count;
                    Status.Text = $"Глава {chapter.Number ?? "—"} · {position + 1}/{queue.Count} · {p.Message}";
                });
                try
                {
                    await downloader.DownloadAsync(manga, chapter, options, progress, cancellation.Token);
                    completed++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add($"Глава {chapter.Number ?? "—"}: {ex.Message}"); }
            }
            if (!closed)
            {
                Progress.Value = 100;
                Status.Text = $"Готово: {completed} из {queue.Count} глав.";
                if (errors.Count > 0) ShowMessage(string.Join(Environment.NewLine, errors));
            }
        }
        catch (OperationCanceledException)
        {
            if (!closed) Status.Text = $"Остановлено. Завершено: {completed}/{queue.Count}. Для докачки запустите выбранные главы снова.";
        }
        finally
        {
            downloadCancellation = null;
            if (!closed) SetDownloading(false);
        }
    }

    private void SetDownloading(bool value)
    {
        downloading = value;
        CancelButton.IsEnabled = value;
        ResultsList.IsEnabled = LanguageBox.IsEnabled = GroupBox.IsEnabled = SearchButton.IsEnabled = SearchBox.IsEnabled = !value;
        ChaptersList.IsEnabled = FolderBox.IsEnabled = CbzCheck.IsEnabled = SaverCheck.IsEnabled = !value;
        MoreButton.IsEnabled = !value && searchOffset > 0 && searchOffset % 30 == 0;
        UpdateSelection();
    }

    private async Task SaveSettingsAsync()
    {
        try { await settings.SaveAsync(new(FolderBox.Text.Trim(), CbzCheck.IsChecked == true, SaverCheck.IsChecked == true, LanguageBox.SelectedValue as string ?? "ru")); }
        catch (Exception ex) { if (!closed) ShowMessage("Не удалось сохранить настройки: " + ex.Message, InfoBarSeverity.Warning); }
    }

    private void ShowMessage(string message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        MessageBar.Severity = severity;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
        if (severity == InfoBarSeverity.Error) Status.Text = "Операция завершилась с ошибкой.";
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        closed = true;
        searchCancellation?.Cancel();
        feedCancellation?.Cancel();
        downloadCancellation?.Cancel();
        api.Dispose();
    }
}
