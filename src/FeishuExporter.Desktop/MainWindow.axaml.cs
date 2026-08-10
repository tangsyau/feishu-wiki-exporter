using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FeishuExporter.Core;
using System.Diagnostics;

namespace FeishuExporter.Desktop;

public partial class MainWindow : Window
{
    private static readonly IBrush AccentBrush = new SolidColorBrush(Color.Parse("#2563EB"));
    private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#22A06B"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#D14343"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#9CA3AF"));
    private static readonly IBrush MutedTextBrush = new SolidColorBrush(Color.Parse("#667085"));

    private CancellationTokenSource? _exportCancellation;
    private bool _connectionValidated;
    private bool _cloudSource;
    private bool _wikiSpaceListAvailable;
    private bool _manualWikiIdExpanded;
    private int _currentStep = 1;
    private string? _lastOutputRoot;
    private bool _exportCompleted;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"版本 {GetApplicationVersion()}";
        OutputPathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Feishu Backup");
        SetSourceType(cloudSource: false);
        UpdateOutputModeVisibility();
        UpdateExportPreview();
        ShowStep(1);
    }

    private static string GetApplicationVersion()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        return version is { Build: >= 0 } ? version.ToString(3) : version?.ToString() ?? "未知";
    }

    private async void TestConnection_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetCredentialButtons(false);
            SetConnectionStatus(null, "正在连接……");
            using var client = CreateClient();
            await client.TestConnectionAsync();
            _connectionValidated = true;
            SetConnectionStatus(true, "连接成功，可以继续选择导出内容");
            UpdateConnectionSummary();
        }
        catch (Exception ex)
        {
            _connectionValidated = false;
            SetConnectionStatus(false, "连接失败，请检查凭证和网络");
            AppendLog("连接失败：" + FormatException(ex));
        }
        finally
        {
            SetCredentialButtons(true);
        }
    }

    private async void ConnectAndContinue_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetCredentialButtons(false);
            SetConnectionStatus(null, "正在连接并读取知识库……");
            using var client = CreateClient();
            await client.TestConnectionAsync();
            _connectionValidated = true;
            try
            {
                var spaces = await client.ListWikiSpacesAsync();
                PopulateSpaces(spaces);
                SetConnectionStatus(true, $"连接成功，读取到 {spaces.Count} 个知识库");
            }
            catch (Exception ex)
            {
                _wikiSpaceListAvailable = false;
                _manualWikiIdExpanded = true;
                UpdateManualSourceVisibility();
                SetConnectionStatus(true, "连接成功；知识库列表暂未读取，可手动填写 ID / Token");
                AppendLog("读取知识库列表失败：" + FormatException(ex));
            }
            UpdateConnectionSummary();
            ShowStep(2);
        }
        catch (Exception ex)
        {
            _connectionValidated = false;
            SetConnectionStatus(false, "连接失败，请检查凭证、权限和网络");
            AppendLog("连接飞书失败：" + FormatException(ex));
        }
        finally
        {
            SetCredentialButtons(true);
        }
    }

    private async void LoadSpaces_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            SetCredentialButtons(false);
            ConnectionSummaryText.Text = "正在刷新知识库列表……";
            using var client = CreateClient();
            var spaces = await client.ListWikiSpacesAsync();
            PopulateSpaces(spaces);
            _connectionValidated = true;
            SetConnectionStatus(true, $"连接成功，读取到 {spaces.Count} 个知识库");
            UpdateConnectionSummary();
        }
        catch (Exception ex)
        {
            if (!_wikiSpaceListAvailable)
            {
                _manualWikiIdExpanded = true;
                UpdateManualSourceVisibility();
            }
            ConnectionSummaryText.Text = "读取知识库失败";
            AppendLog("读取知识库失败：" + FormatException(ex));
        }
        finally
        {
            SetCredentialButtons(true);
            LoadSpacesButton.IsEnabled = !_cloudSource;
        }
    }

    private void PopulateSpaces(IReadOnlyList<WikiSpace> spaces)
    {
        var previousId = (SpaceBox.SelectedItem as SpaceOption)?.Id;
        var options = spaces.Select(x => new SpaceOption(x.SpaceId, x.Name)).ToList();
        SpaceBox.ItemsSource = options;
        SpaceBox.SelectedItem = options.FirstOrDefault(x => x.Id == previousId);
        if (SpaceBox.SelectedItem is null && options.Count > 0)
        {
            SpaceBox.SelectedIndex = 0;
        }
        _wikiSpaceListAvailable = options.Count > 0;
        _manualWikiIdExpanded = !_wikiSpaceListAvailable || !string.IsNullOrWhiteSpace(SourceIdBox.Text);
        UpdateManualSourceVisibility();
    }

    private async void ChooseOutput_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择导出目录",
            AllowMultiple = false
        });
        if (folders.Count > 0)
        {
            OutputPathBox.Text = folders[0].Path.LocalPath;
        }
    }

    private void SelectWikiSource_Click(object? sender, RoutedEventArgs e) => SetSourceType(cloudSource: false);

    private void SelectCloudSource_Click(object? sender, RoutedEventArgs e) => SetSourceType(cloudSource: true);

    private void ToggleManualSource_Click(object? sender, RoutedEventArgs e)
    {
        if (_cloudSource)
        {
            return;
        }
        _manualWikiIdExpanded = !_manualWikiIdExpanded;
        UpdateManualSourceVisibility();
        if (_manualWikiIdExpanded)
        {
            SourceIdBox.Focus();
        }
    }

    private void StepOne_Click(object? sender, RoutedEventArgs e) => ShowStep(1);

    private void StepTwo_Click(object? sender, RoutedEventArgs e)
    {
        if (!_connectionValidated)
        {
            ShowStep(1);
            SetConnectionStatus(false, "请先测试连接，或使用“连接并继续”");
            return;
        }

        UpdateExportPreview();
        ShowStep(2);
    }

    private void StepThree_Click(object? sender, RoutedEventArgs e)
    {
        if (!_connectionValidated)
        {
            StepTwo_Click(sender, e);
            return;
        }

        try
        {
            ValidateSourceSelection();
            UpdateSourceSummary();
            ShowStep(3);
        }
        catch (Exception ex)
        {
            ShowStep(2);
            var details = FormatException(ex);
            ShowSourceValidation(details);
            AppendLog("无法进入导出设置：" + details);
        }
    }

    private void BackToConnection_Click(object? sender, RoutedEventArgs e) => ShowStep(1);

    private void BackToSource_Click(object? sender, RoutedEventArgs e)
    {
        UpdateExportPreview();
        ShowStep(2);
    }

    private void ContinueToSettings_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            ValidateSourceSelection();
            ShowSourceValidation(null);
            UpdateExportPreview();
            UpdateSourceSummary();
            ShowStep(3);
        }
        catch (Exception ex)
        {
            var details = FormatException(ex);
            ShowSourceValidation(details);
            AppendLog("请完善导出来源：" + details);
        }
    }

    private async void StartExport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            ValidateSourceSelection();
            ResetCompletionState();
            SetRunning(true);
            LogBox.Text = string.Empty;
            ExportProgressBar.Value = 0;
            _exportCancellation = new CancellationTokenSource();
            var credentials = ReadCredentials();
            var sourceType = _cloudSource ? ExportSourceType.CloudFolder : ExportSourceType.Wiki;
            var sourceId = ResolveSourceId();
            var outputPath = OutputPathBox.Text!;
            var outputMode = GetOutputMode();
            var includesReader = outputMode is ExportOutputMode.Reader or ExportOutputMode.ReaderAndOffice;
            var includesOffice = outputMode is ExportOutputMode.Office or ExportOutputMode.ReaderAndOffice;

            var policy = GetTag(ExistingPolicyBox) switch
            {
                "overwrite" => ExistingFilePolicy.Overwrite,
                "keep-both" => ExistingFilePolicy.KeepBoth,
                _ => ExistingFilePolicy.Skip
            };
            var options = new ExportOptions
            {
                Credentials = credentials,
                SourceType = sourceType,
                SourceId = sourceId,
                ExportRoot = Path.GetFullPath(outputPath),
                OutputMode = outputMode,
                DocumentFormat = GetTag(FormatBox) ?? "docx",
                ExistingFilePolicy = policy,
                EmbeddedAttachmentPlacement = GetTag(AttachmentPlacementBox) == "subfolder"
                    ? EmbeddedAttachmentPlacement.DocumentSubfolder
                    : EmbeddedAttachmentPlacement.AlongsideDocument,
                TreatWikiParentsAsNavigationFolders = includesOffice && NavigationPagesBox.IsChecked == true,
                SkipUnchanged = IncrementalBox.IsChecked == true,
                DownloadAttachments = includesOffice && AttachmentsBox.IsChecked == true,
                MaxParallelism = int.Parse(GetTag(ParallelBox) ?? "2")
            };

            using var client = new FeishuApiClient(credentials);
            var engine = new ExportEngine(client);
            var progress = new Progress<ExportProgress>(UpdateProgress);
            var preparation = await engine.PrepareAsync(options, progress, _exportCancellation.Token);
            foreach (var warning in preparation.Warnings)
            {
                AppendLog("警告：" + warning);
            }

            IReadOnlySet<string> navigationPagesToSkip = new HashSet<string>(StringComparer.Ordinal);
            if (includesOffice && options.TreatWikiParentsAsNavigationFolders && preparation.NavigationCandidates.Count > 0)
            {
                var likelyCount = preparation.NavigationCandidates.Count(candidate =>
                    candidate.Classification == NavigationPageClassification.LikelyNavigation);
                var uncertainCount = preparation.NavigationCandidates.Count - likelyCount;
                StartButton.Content = "等待确认……";
                ProgressText.Text = $"发现疑似导航页 {likelyCount} 个、待确认 {uncertainCount} 个，请审核跳过清单";
                var reviewWindow = new NavigationReviewWindow(preparation.NavigationCandidates);
                var selected = await reviewWindow.ShowDialog<IReadOnlySet<string>?>(this);
                if (selected is null)
                {
                    ProgressText.Text = "已取消，尚未开始下载";
                    AppendLog("已取消导航页清单审核，本次没有开始下载。");
                    StartButton.Content = "开始导出";
                    return;
                }

                navigationPagesToSkip = selected;
                AppendLog($"导航页审核完成：将跳过 {selected.Count} 个 Office 文档，保留导出 {preparation.NavigationCandidates.Count - selected.Count} 个待审核文档。");
            }
            else if (includesOffice && options.TreatWikiParentsAsNavigationFolders)
            {
                AppendLog("未发现符合规则的疑似导航页。所有文档将正常导出。");
            }

            ExportSummary? officeSummary = null;
            DirectOfflineKnowledgeBuildResult? readerSummary = null;
            if (includesOffice)
            {
                StartButton.Content = "正在导出 Office 文档……";
                officeSummary = await engine.ExportPreparedAsync(
                    options,
                    preparation,
                    navigationPagesToSkip,
                    progress,
                    _exportCancellation.Token);
                AppendLog($"Office 目录：{officeSummary.OutputDirectory}");
                AppendLog("Office 详细结果已写入 export-report.csv。");
            }

            if (includesReader)
            {
                ProgressText.Text = "正在生成 Reader 离线包……";
                var builder = new DirectOfflineKnowledgeBuilder(client);
                var offlineProgress = new Progress<OfflineKnowledgeProgress>(item =>
                {
                    ProgressText.Text = item.Total > 0
                        ? $"正在生成 Reader 离线包：{item.Completed}/{item.Total}　{item.CurrentItem}"
                        : "正在生成 Reader 离线包……";
                });
                readerSummary = await builder.BuildAsync(
                    options,
                    preparation,
                    offlineProgress,
                    _exportCancellation.Token);
                AppendLog($"Reader 离线包：{readerSummary.OutputDirectory}");
                AppendLog($"Reader 收录 {readerSummary.Pages} 个页面、{readerSummary.Attachments} 个附件；未支持块 {readerSummary.UnsupportedBlocks} 个，本次复用 {readerSummary.ReusedPages} 个页面。");
            }

            var summaryParts = new List<string>();
            if (readerSummary is not null)
            {
                summaryParts.Add($"Reader 页面 {readerSummary.Pages}");
                summaryParts.Add($"附件 {readerSummary.Attachments}");
                summaryParts.Add($"未支持块 {readerSummary.UnsupportedBlocks}");
            }
            if (officeSummary is not null)
            {
                summaryParts.Add($"Office 成功 {officeSummary.Succeeded}");
                summaryParts.Add($"跳过 {officeSummary.Skipped}");
                summaryParts.Add($"失败 {officeSummary.Failed}");
            }
            CountText.Text = string.Join("　", summaryParts);
            ExportProgressBar.IsIndeterminate = false;
            ExportProgressBar.Value = 100;
            ProgressText.Text = "导出完成";
            _lastOutputRoot = Path.GetFullPath(outputPath);
            SetCompletedState();
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "已取消；已完成的文件可以在下次继续";
            AppendLog("用户取消了导出。已完成项目的增量状态已保留。");
            StartButton.IsVisible = true;
            StartButton.Content = "继续导出";
        }
        catch (Exception ex)
        {
            ProgressText.Text = "导出未完成";
            AppendLog("错误：" + FormatException(ex));
            StartButton.IsVisible = true;
            StartButton.Content = "重新导出";
        }
        finally
        {
            _exportCancellation?.Dispose();
            _exportCancellation = null;
            SetRunning(false);
        }
    }

    private void CancelExport_Click(object? sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        ProgressText.Text = "正在安全停止……";
        _exportCancellation?.Cancel();
    }

    private void OutputMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (FormatBox is null)
        {
            return;
        }
        UpdateOutputModeVisibility();
        UpdateExportPreview();
    }

    private void ReturnToSettings_Click(object? sender, RoutedEventArgs e)
    {
        ResetCompletionState();
        ProgressText.Text = "尚未开始";
        CountText.Text = string.Empty;
        ExportProgressBar.Value = 0;
        LogBox.Text = string.Empty;
    }

    private void ShowSummary_Click(object? sender, RoutedEventArgs e)
    {
        LogExpander.IsExpanded = true;
    }

    private void OpenOutput_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutputRoot) || !Directory.Exists(_lastOutputRoot))
        {
            ProgressText.Text = "输出目录不存在或已被移动";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _lastOutputRoot,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog("无法打开输出位置：" + FormatException(ex));
            LogExpander.IsExpanded = true;
        }
    }

    private FeishuApiClient CreateClient() => new(ReadCredentials());

    private FeishuCredentials ReadCredentials()
    {
        var appId = AppIdBox.Text?.Trim();
        var secret = AppSecretBox.Text;
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("App ID 和 App Secret 都是必填项。");
        }
        return new FeishuCredentials(appId, secret);
    }

    private string ResolveSourceId()
    {
        var manualId = SourceIdBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(manualId) && (_cloudSource || ManualIdPanel.IsVisible))
        {
            return manualId;
        }

        if (!_cloudSource && SpaceBox.SelectedItem is SpaceOption selectedSpace)
        {
            return selectedSpace.Id;
        }

        throw new ArgumentException(_cloudSource
            ? "请输入云空间文件夹的 Folder Token。"
            : "请选择知识库，或手动填写知识库 ID。");
    }

    private void ValidateSourceSelection()
    {
        _ = ReadCredentials();
        _ = ResolveSourceId();
        if (string.IsNullOrWhiteSpace(OutputPathBox.Text))
        {
            throw new ArgumentException("请选择导出目录。");
        }
    }

    private void SetSourceType(bool cloudSource)
    {
        _cloudSource = cloudSource;
        ShowSourceValidation(null);
        SetSelectedClass(WikiSourceButton, !cloudSource);
        SetSelectedClass(CloudSourceButton, cloudSource);
        SpaceBox.IsEnabled = !cloudSource;
        LoadSpacesButton.IsEnabled = !cloudSource;
        NavigationPagesBox.IsEnabled = !cloudSource && IncludesOfficeOutput();
        if (!cloudSource && !string.IsNullOrWhiteSpace(SourceIdBox.Text))
        {
            _manualWikiIdExpanded = true;
        }
        SourceIdBox.PlaceholderText = cloudSource
            ? "必填：云空间文件夹的 Folder Token"
            : "可选：知识库 ID";
        SourceIdHintText.Text = cloudSource
            ? "导出云空间文件夹时必须填写 Folder Token。"
            : "选择知识库后可以留空；手动填写时优先使用这里的 ID。";
        UpdateManualSourceVisibility();
    }

    private void UpdateManualSourceVisibility()
    {
        if (_cloudSource)
        {
            ManualIdLabel.Text = "Folder Token";
            ManualIdToggleButton.IsVisible = false;
            ManualIdPanel.IsVisible = true;
            return;
        }

        var showManualId = !_wikiSpaceListAvailable || _manualWikiIdExpanded;
        ManualIdLabel.Text = showManualId ? "知识库 ID" : "其他方式";
        ManualIdToggleButton.IsVisible = _wikiSpaceListAvailable;
        ManualIdToggleButton.Content = showManualId ? "收起手动填写" : "手动填写知识库 ID";
        ManualIdPanel.IsVisible = showManualId;
    }

    private void ShowSourceValidation(string? message)
    {
        SourceValidationText.Text = message ?? string.Empty;
        SourceValidationText.IsVisible = !string.IsNullOrWhiteSpace(message);
    }

    private void SetConnectionStatus(bool? success, string message)
    {
        ConnectionStatusDot.Background = success switch
        {
            true => SuccessBrush,
            false => ErrorBrush,
            null => AccentBrush
        };
        ConnectionStatusText.Text = message;
        ConnectionStatusText.Foreground = success switch
        {
            true => SuccessBrush,
            false => ErrorBrush,
            null => MutedTextBrush
        };
        StepOneStateText.Text = success switch
        {
            true => "连接成功",
            false => "连接失败",
            null => "正在连接"
        };
    }

    private void UpdateConnectionSummary()
    {
        var appId = AppIdBox.Text?.Trim() ?? string.Empty;
        var masked = appId.Length > 10 ? appId[..8] + "••••" : appId;
        ConnectionSummaryText.Text = $"已连接飞书　App ID：{masked}";
    }

    private void UpdateExportPreview()
    {
        FormatPreviewText.Text = GetOutputMode() switch
        {
            ExportOutputMode.Reader => "Reader 离线包",
            ExportOutputMode.Office => $"Office {(GetTag(FormatBox) ?? "docx").ToUpperInvariant()}",
            _ => $"Reader + {(GetTag(FormatBox) ?? "docx").ToUpperInvariant()}"
        };
        var includesOffice = IncludesOfficeOutput();
        AttachmentsPreviewText.Text = includesOffice
            ? AttachmentsBox.IsChecked == true ? "Office 附件启用" : "Office 附件关闭"
            : "Reader 必要附件";
        AttachmentsPreviewText.Foreground = SuccessBrush;
        PlacementPreviewText.Text = includesOffice
            ? GetTag(AttachmentPlacementBox) == "subfolder"
                ? "主文档同名子文件夹"
                : "与文档相同目录"
            : "紧凑离线包";
    }

    private ExportOutputMode GetOutputMode() => GetTag(OutputModeBox) switch
    {
        "office" => ExportOutputMode.Office,
        "both" => ExportOutputMode.ReaderAndOffice,
        _ => ExportOutputMode.Reader
    };

    private bool IncludesOfficeOutput() =>
        GetOutputMode() is ExportOutputMode.Office or ExportOutputMode.ReaderAndOffice;

    private void UpdateOutputModeVisibility()
    {
        if (OutputModeBox is null || OfficeSettingsPanel is null)
        {
            return;
        }
        var mode = GetOutputMode();
        var includesReader = mode is ExportOutputMode.Reader or ExportOutputMode.ReaderAndOffice;
        var includesOffice = mode is ExportOutputMode.Office or ExportOutputMode.ReaderAndOffice;
        ReaderModeNotice.IsVisible = includesReader;
        OfficeSettingsPanel.IsVisible = includesOffice;
        NavigationPagesBox.IsEnabled = includesOffice && !_cloudSource;
    }

    private void SetCompletedState()
    {
        _exportCompleted = true;
        StartButton.IsVisible = false;
        CancelButton.IsVisible = false;
        SettingsBackButton.IsVisible = false;
        OpenOutputButton.IsVisible = true;
        SummaryButton.IsVisible = true;
        ReturnSettingsButton.IsVisible = true;
    }

    private void ResetCompletionState()
    {
        _exportCompleted = false;
        StartButton.IsVisible = true;
        StartButton.Content = "开始导出";
        CancelButton.IsVisible = true;
        SettingsBackButton.IsVisible = true;
        OpenOutputButton.IsVisible = false;
        SummaryButton.IsVisible = false;
        ReturnSettingsButton.IsVisible = false;
        StepOneButton.IsEnabled = true;
        StepTwoButton.IsEnabled = true;
        StepThreeButton.IsEnabled = true;
    }

    private void UpdateSourceSummary()
    {
        var sourceName = _cloudSource
            ? "云空间文件夹"
            : (SpaceBox.SelectedItem as SpaceOption)?.Name ?? "知识库";
        ExportSourceSummaryText.Text = $"来源：{sourceName}　　导出到：{OutputPathBox.Text}";
    }

    private void ShowStep(int step)
    {
        _currentStep = Math.Clamp(step, 1, 3);
        ConnectionPage.IsVisible = _currentStep == 1;
        SourcePage.IsVisible = _currentStep == 2;
        SettingsPage.IsVisible = _currentStep == 3;
        SetSelectedClass(StepOneButton, _currentStep == 1);
        SetSelectedClass(StepTwoButton, _currentStep == 2);
        SetSelectedClass(StepThreeButton, _currentStep == 3);

        StepOneBadge.Background = _currentStep == 1 ? AccentBrush : _connectionValidated ? SuccessBrush : MutedBrush;
        StepTwoBadge.Background = _currentStep == 2 ? AccentBrush : _currentStep > 2 ? SuccessBrush : MutedBrush;
        StepThreeBadge.Background = _currentStep == 3 ? AccentBrush : MutedBrush;
        StepTwoBadgeText.Foreground = _currentStep >= 2 ? Brushes.White : MutedTextBrush;
        StepThreeBadgeText.Foreground = _currentStep == 3 ? Brushes.White : MutedTextBrush;
    }

    private void UpdateProgress(ExportProgress progress)
    {
        var status = progress.Status switch
        {
            ExportItemStatus.Pending => "准备中",
            ExportItemStatus.Exporting => "导出中",
            ExportItemStatus.Completed => "已完成",
            ExportItemStatus.Skipped => "已跳过",
            ExportItemStatus.Unsupported => "暂不支持",
            ExportItemStatus.Failed => "失败",
            _ => progress.Status.ToString()
        };
        ProgressText.Text = progress.Message is null
            ? $"{status}：{progress.CurrentItem}"
            : $"{status}：{progress.CurrentItem} — {progress.Message}";
        CountText.Text = progress.Total == 0
            ? string.Empty
            : $"{progress.Completed}/{progress.Total}　成功 {progress.Succeeded}　跳过 {progress.Skipped}　失败 {progress.Failed}";
        ExportProgressBar.IsIndeterminate = progress.Total == 0;
        if (progress.Total > 0)
        {
            ExportProgressBar.Value = progress.Completed * 100d / progress.Total;
        }
        if (progress.Status is ExportItemStatus.Failed or ExportItemStatus.Unsupported)
        {
            AppendLog(ProgressText.Text ?? string.Empty);
        }
    }

    private static string? GetTag(ComboBox comboBox) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private static string FormatException(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            var message = $"{current.GetType().Name}: {current.Message}";
            if (messages.Count == 0 || !string.Equals(messages[^1], message, StringComparison.Ordinal))
            {
                messages.Add(message);
            }
        }

        return string.Join(" → ", messages);
    }

    private static void SetSelectedClass(Button button, bool selected)
    {
        if (selected)
        {
            if (!button.Classes.Contains("selected"))
            {
                button.Classes.Add("selected");
            }
        }
        else
        {
            button.Classes.Remove("selected");
        }
    }

    private void SetCredentialButtons(bool enabled)
    {
        TestButton.IsEnabled = enabled;
        ConnectButton.IsEnabled = enabled;
        LoadSpacesButton.IsEnabled = enabled && !_cloudSource;
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        TestButton.IsEnabled = !running;
        ConnectButton.IsEnabled = !running;
        LoadSpacesButton.IsEnabled = !running && !_cloudSource;
        StepOneButton.IsEnabled = !running && !_exportCompleted;
        StepTwoButton.IsEnabled = !running && !_exportCompleted;
        StepThreeButton.IsEnabled = !running && !_exportCompleted;
        ExportProgressBar.IsIndeterminate = running;
        if (running)
        {
            ShowStep(3);
            StartButton.Content = "正在导出……";
            ProgressText.Text = "准备导出……";
            CountText.Text = string.Empty;
        }
        else
        {
            ExportProgressBar.IsIndeterminate = false;
        }
    }

    private void AppendLog(string message)
    {
        LogBox.Text = string.IsNullOrEmpty(LogBox.Text)
            ? message
            : LogBox.Text + Environment.NewLine + message;
        LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
    }

    private sealed record SpaceOption(string Id, string Name)
    {
        public override string ToString() => $"{Name}（{Id}）";
    }
}
