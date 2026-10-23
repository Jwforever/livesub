using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using LiveSub.Services;

namespace LiveSub;

public sealed class ModelRow : INotifyPropertyChanged
{
    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(48, 209, 88));
    private static readonly Brush BlueBrush = new SolidColorBrush(Color.FromRgb(90, 200, 250));
    private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(255, 159, 10));
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(143, 160, 179));

    private bool _isCurrent;
    private bool _isBusy;
    private double _progress;
    private string _detail = "";
    private string? _notice;
    private CancellationTokenSource? _cts;

    public ModelRow(ModelDescriptor model)
    {
        Model = model;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ModelDescriptor Model { get; }
    public string Name => Model.DisplayName;
    public string Size => Model.SizeLabel;
    public string Latency => Model.Latency;
    public string Description => Model.Description;
    public bool IsDownloaded => Model.IsDownloaded;
    public bool IsCurrent => _isCurrent;
    public bool IsBusy => _isBusy;
    public double Progress => _progress;
    public string Detail => _detail;

    public string StateText
    {
        get
        {
            if (_isBusy) return $"下载中 {_progress:0}%";
            if (!string.IsNullOrEmpty(_notice)) return _notice;
            if (_isCurrent) return Model.IsDownloaded ? "使用中" : "当前选择 · 未下载";
            return Model.IsDownloaded ? "已下载" : "未下载";
        }
    }

    public Brush StateBrush
    {
        get
        {
            if (_isBusy) return BlueBrush;
            if (!string.IsNullOrEmpty(_notice)) return _notice.Contains("失败", StringComparison.Ordinal) ? OrangeBrush : GreenBrush;
            if (_isCurrent) return Model.IsDownloaded ? GreenBrush : OrangeBrush;
            return Model.IsDownloaded ? BlueBrush : OrangeBrush;
        }
    }

    public Visibility CurrentVisibility => _isCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DownloadVisibility => !_isBusy && !Model.IsDownloaded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UseVisibility => !_isBusy && Model.IsDownloaded && !_isCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DeleteVisibility => !_isBusy && Model.IsDownloaded && !_isCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;
    public string DownloadId => $"{Model.Id}-download";
    public string CancelId => $"{Model.Id}-cancel";
    public string UseId => $"{Model.Id}-use";
    public string DeleteId => $"{Model.Id}-delete";

    public void SetCurrent(bool current)
    {
        _isCurrent = current;
        _notice = null;
        RaiseAll();
    }

    public void BeginDownload(CancellationTokenSource cts)
    {
        _cts = cts;
        _isBusy = true;
        _progress = 0;
        _detail = "准备下载…";
        _notice = null;
        RaiseAll();
    }

    public void Cancel() => _cts?.Cancel();

    public void ApplyProgress(ModelDownloadProgress progress)
    {
        if (!_isBusy) return;
        _progress = progress.Total > 0
            ? Math.Clamp(progress.Received * 100.0 / progress.Total, 0, 100)
            : 0;
        var received = FormatBytes(progress.Received);
        var total = progress.Total > 0 ? FormatBytes(progress.Total) : "未知大小";
        var speed = progress.BytesPerSecond > 0 ? $" · {FormatBytes((long)progress.BytesPerSecond)}/s" : "";
        _detail = $"{received} / {total}{speed}";
        RaiseAll();
    }

    public void EndDownload(bool success, string? notice)
    {
        _isBusy = false;
        _detail = "";
        _progress = success ? 100 : 0;
        _notice = notice;
        _cts = null;
        RaiseAll();
    }

    public void NotifyFileChanged(string? notice = null)
    {
        _notice = notice;
        _progress = 0;
        _detail = "";
        RaiseAll();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}

public partial class ModelsWindow : Window
{
    public ObservableCollection<ModelRow> AsrRows { get; } = new();
    public ObservableCollection<ModelRow> MtRows { get; } = new();

    private IReadOnlyList<ModelDescriptor> _asrCatalog = Array.Empty<ModelDescriptor>();

    public ModelsWindow()
    {
        InitializeComponent();

        _asrCatalog = ModelCatalog.GetAsrModels();
        foreach (var model in _asrCatalog)
            AsrRows.Add(new ModelRow(model));
        foreach (var model in ModelCatalog.GetTranslationModels())
            MtRows.Add(new ModelRow(model));

        AsrList.ItemsSource = AsrRows;
        MtList.ItemsSource = MtRows;
        ApplySelection(ModelSelectionStore.Load());
        FooterText.Text = "选择已下载的模型即可切换；正在使用的模型需先切换后才能删除。";
        RefreshSummary();
        Loaded += OnLoaded;
        Loaded += async (_, _) => await RefreshAsrFromSourceAsync();
        Closed += (_, _) =>
        {
            foreach (var row in AsrRows) row.Cancel();
            foreach (var row in MtRows) row.Cancel();
        };
    }

    private async Task RefreshAsrFromSourceAsync()
    {
        try
        {
            FooterText.Text = "正在从 hf-mirror 加速源获取识别模型列表（最多 8 秒）…";
            var dynamic = await ModelCatalog.LoadAsrFromSourceAsync(CancellationToken.None);
            _asrCatalog = ModelCatalog.MergeAsrWithSource(dynamic);
            AsrRows.Clear();
            foreach (var model in _asrCatalog)
                AsrRows.Add(new ModelRow(model));
            ApplySelection(ModelSelectionStore.Load());
            FooterText.Text = $"已获取 {dynamic.Count} 个识别模型（加速源优先，失败时使用缓存或内置列表）。";
        }
        catch (Exception ex)
        {
            AppPaths.Log("Model source refresh failed: " + ex.Message);
            FooterText.Text = "无法连接模型源，使用内置模型列表。";
        }
    }

    private void RebuildRows()
    {
        var selection = ModelSelectionStore.Load();
        _asrCatalog = ModelCatalog.GetAsrModels();
        AsrRows.Clear();
        foreach (var model in _asrCatalog)
            AsrRows.Add(new ModelRow(model));
        MtRows.Clear();
        foreach (var model in ModelCatalog.GetTranslationModels())
            MtRows.Add(new ModelRow(model));
        ApplySelection(selection);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.ApplyDarkFrame(this);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var work = WindowEffects.GetWorkArea(this);
        Width = Math.Min(Width, work.Width - 16);
        Height = Math.Min(Height, work.Height - 16);
        MinWidth = Math.Min(MinWidth, Width);
        MinHeight = Math.Min(MinHeight, Height);

        if (Owner != null && Owner.WindowState == WindowState.Normal)
        {
            Left = Owner.Left + (Owner.Width - Width) / 2;
            Top = Owner.Top + (Owner.Height - Height) / 2;
        }
        else
        {
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
        }

        if (Left < work.Left) Left = work.Left;
        if (Top < work.Top) Top = work.Top;
        if (Left + Width > work.Right) Left = work.Right - Width;
        if (Top + Height > work.Bottom) Top = work.Bottom - Height;
    }

    private void ApplySelection(ModelSelection selection)
    {
        foreach (var row in AsrRows) row.SetCurrent(row.Model.Id == selection.AsrId);
        foreach (var row in MtRows) row.SetCurrent(row.Model.Id == selection.TranslationId);
    }

    private void RefreshSummary()
    {
        var selection = ModelSelectionStore.Load();
        var asr = ModelCatalog.GetAsr(selection.AsrId);
        var mt = ModelCatalog.GetTranslation(selection.TranslationId);
        var asrSuffix = asr.IsDownloaded ? "" : "（未下载）";
        var mtSuffix = mt.IsDownloaded ? "" : "（未下载）";
        CurrentSummary.Text = $"当前：{asr.DisplayName}{asrSuffix}  +  {mt.DisplayName}{mtSuffix}";
        CurrentPathText.Text = $"模型目录：{AppPaths.ModelsRoot}";
    }

    private static ModelRow? GetRow(object sender) =>
        (sender as FrameworkElement)?.DataContext as ModelRow;

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var row = GetRow(sender);
        if (row == null || row.IsBusy || row.IsDownloaded) return;

        using var cts = new CancellationTokenSource();
        row.BeginDownload(cts);
        FooterText.Text = $"正在下载 {row.Model.DisplayName}…（支持断点续传）";
        var progress = new Progress<ModelDownloadProgress>(row.ApplyProgress);

        try
        {
            await ModelDownloadService.DownloadAsync(row.Model, progress, cts.Token);
            var notice = row.IsCurrent ? "已下载，可直接开始" : "下载完成，点“使用”启用";
            row.EndDownload(true, notice);
            FooterText.Text = $"{row.Model.DisplayName} 下载完成。";
            RefreshSummary();
        }
        catch (OperationCanceledException)
        {
            row.EndDownload(false, "已取消");
            FooterText.Text = "下载已取消。";
        }
        catch (Exception ex)
        {
            AppPaths.Log("Model download failed: " + ex);
            row.EndDownload(false, "下载失败");
            FooterText.Text = "下载失败，可重试或更换网络。";
            MessageBox.Show(this, ex.Message, "模型下载失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        var row = GetRow(sender);
        row?.Cancel();
        FooterText.Text = "正在取消下载…";
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        var row = GetRow(sender);
        if (row == null || !row.IsDownloaded || row.IsBusy) return;

        ModelSelectionStore.Select(row.Model);
        ApplySelection(ModelSelectionStore.Load());
        RefreshSummary();
        FooterText.Text = $"已切换为 {row.Model.DisplayName}，下次开始字幕后生效。";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var row = GetRow(sender);
        if (row == null || row.IsBusy || row.IsCurrent || !row.IsDownloaded) return;

        var answer = MessageBox.Show(
            this,
            $"确定删除 {row.Model.DisplayName}？\n\n{row.Model.FullPath}\n\n删除后可重新下载。",
            "删除模型",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        ModelDownloadService.Delete(row.Model);
        row.NotifyFileChanged("已删除");
        RefreshSummary();
        FooterText.Text = $"{row.Model.DisplayName} 已删除。";
    }

    private void AddModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddModelWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            RebuildRows();
            RefreshSummary();
            FooterText.Text = $"已添加自定义模型：{dialog.ModelName}，下载后即可使用。";
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ModelsRoot);
            Process.Start(new ProcessStartInfo(AppPaths.ModelsRoot) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppPaths.Log("Open models folder failed: " + ex.Message);
        }
    }
}
