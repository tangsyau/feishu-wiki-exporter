using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace FeishuExporter.Core;

public sealed class ExportEngine(FeishuApiClient apiClient)
{
    public async Task<ExportSummary> ExportAsync(
        ExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var preparation = await PrepareAsync(options, progress, cancellationToken);
        var navigationPagesToSkip = options.TreatWikiParentsAsNavigationFolders
            ? preparation.NavigationCandidates
                .Where(candidate => candidate.DefaultSkip)
                .Select(candidate => candidate.HierarchyToken)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        return await ExportPreparedAsync(
            options,
            preparation,
            navigationPagesToSkip,
            progress,
            cancellationToken);
    }

    public async Task<ExportSummary> ExportPreparedAsync(
        ExportOptions options,
        ExportPreparation preparation,
        IReadOnlySet<string> navigationPagesToSkip,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        if (options.OutputMode == ExportOutputMode.Reader)
        {
            throw new InvalidOperationException("仅生成 Reader 离线包时不应调用 Office 导出流程。");
        }
        Directory.CreateDirectory(options.ExportRoot);

        var sourceName = preparation.SourceName;
        IReadOnlyList<ExportItem> items = preparation.Items;
        if (options.SourceType == ExportSourceType.Wiki &&
            options.TreatWikiParentsAsNavigationFolders &&
            navigationPagesToSkip.Count > 0)
        {
            var suggestedTokens = preparation.NavigationCandidates
                .Select(candidate => candidate.HierarchyToken)
                .ToHashSet(StringComparer.Ordinal);
            var approvedTokens = navigationPagesToSkip
                .Where(suggestedTokens.Contains)
                .ToHashSet(StringComparer.Ordinal);
            items = PathPlanner.MarkWikiNavigationFolders(items, approvedTokens);
        }
        var sourceDirectory = Path.Combine(options.ExportRoot, PathPlanner.SanitizeSegment(sourceName));
        Directory.CreateDirectory(sourceDirectory);
        var stateDirectory = ExportStateLayout.GetSourceStateDirectory(options);
        Directory.CreateDirectory(stateDirectory);

        await NavigationAnalysisStore.SaveAsync(
            sourceDirectory,
            preparation.NavigationAnalyses,
            cancellationToken);
        File.Copy(
            Path.Combine(sourceDirectory, ".feishu-export", "navigation-analysis.json"),
            Path.Combine(stateDirectory, "navigation-analysis.json"),
            overwrite: true);

        var planned = PathPlanner.Plan(
            items,
            options.DocumentFormat,
            options.DownloadAttachments,
            options.EmbeddedAttachmentPlacement);
        foreach (var folder in planned.Where(x => x.Item.IsFolder))
        {
            Directory.CreateDirectory(Path.Combine(sourceDirectory, folder.RelativePath));
        }

        var workItems = planned.Where(x => !x.Item.IsFolder).ToList();
        var sourceKey = $"{options.SourceType}:{options.SourceId}";
        var statePath = Path.Combine(stateDirectory, "office-state.json");
        ExportStateLayout.TryMigrateFile(
            Path.Combine(sourceDirectory, ".feishu-export", "state.json"),
            statePath);
        var state = new ExportStateStore(statePath);
        await state.InitializeAsync(cancellationToken);
        if (options.SourceType == ExportSourceType.Wiki && options.TreatWikiParentsAsNavigationFolders)
        {
            await RetireTrackedNavigationPagesAsync(
                planned.Where(item => item.Item.IsNavigationOnly),
                sourceKey,
                sourceDirectory,
                state,
                progress,
                cancellationToken);
        }

        var succeeded = 0;
        var skipped = 0;
        var unsupported = 0;
        var failed = preparation.Failures.Count;
        var completed = preparation.Failures.Count;
        var total = workItems.Count + preparation.Failures.Count;
        var failures = new ConcurrentBag<ExportFailure>(preparation.Failures);

        progress?.Report(CreateProgress(completed, total, 0, 0, failed, "准备开始导出", ExportItemStatus.Pending));

        await Parallel.ForEachAsync(
            workItems,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(options.MaxParallelism, 1, 4),
                CancellationToken = cancellationToken
            },
            async (plannedItem, token) =>
            {
                var item = plannedItem;
                var formatKey = item.ExportExtension ?? item.Item.Type;
                var targetPath = Path.Combine(sourceDirectory, item.RelativePath);

                if (item.ExportExtension is null)
                {
                    Interlocked.Increment(ref unsupported);
                    var done = Interlocked.Increment(ref completed);
                    await state.SaveAsync(sourceKey, item, formatKey, ExportItemStatus.Unsupported, null,
                        "当前版本不支持这种文档类型。", token);
                    progress?.Report(CreateProgress(done, total, succeeded, skipped, failed,
                        item.Item.Title, ExportItemStatus.Unsupported, $"不支持类型：{item.Item.Type}"));
                    return;
                }

                if (options.SkipUnchanged)
                {
                    var currentPath = await state.GetCurrentPathAsync(sourceKey, item, formatKey, sourceDirectory, token);
                    if (currentPath is not null)
                    {
                        Interlocked.Increment(ref skipped);
                        var done = Interlocked.Increment(ref completed);
                        progress?.Report(CreateProgress(done, total, succeeded, skipped, failed,
                            item.Item.Title, ExportItemStatus.Skipped, "自上次成功导出后没有修改"));
                        return;
                    }
                }

                if (File.Exists(targetPath))
                {
                    if (options.ExistingFilePolicy == ExistingFilePolicy.Skip)
                    {
                        Interlocked.Increment(ref skipped);
                        var done = Interlocked.Increment(ref completed);
                        progress?.Report(CreateProgress(done, total, succeeded, skipped, failed,
                            item.Item.Title, ExportItemStatus.Skipped, "目标文件已存在"));
                        return;
                    }

                    if (options.ExistingFilePolicy == ExistingFilePolicy.KeepBoth)
                    {
                        targetPath = GetAvailableVersionPath(targetPath);
                        item = plannedItem with { RelativePath = Path.GetRelativePath(sourceDirectory, targetPath) };
                    }
                }

                var partPath = targetPath + ".part";
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    TryDeletePartFile(partPath);
                    progress?.Report(CreateProgress(completed, total, succeeded, skipped, failed,
                        item.Item.Title, ExportItemStatus.Exporting));

                    if (string.Equals(item.Item.Type, "embedded_file", StringComparison.OrdinalIgnoreCase))
                    {
                        await apiClient.DownloadMediaAsync(item.Item.ContentToken, partPath, token);
                    }
                    else if (string.Equals(item.Item.Type, "file", StringComparison.OrdinalIgnoreCase))
                    {
                        await apiClient.DownloadFileAsync(item.Item.ContentToken, partPath, token);
                    }
                    else
                    {
                        var fileToken = await apiClient.CreateAndWaitForExportAsync(
                            item.ExportExtension!, item.Item.ContentToken, item.Item.Type, token);
                        await apiClient.DownloadExportAsync(fileToken, partPath, token);
                    }

                    var info = new FileInfo(partPath);
                    if (!info.Exists || info.Length == 0)
                    {
                        throw new IOException("下载结果为空。");
                    }

                    File.Move(partPath, targetPath, overwrite: options.ExistingFilePolicy == ExistingFilePolicy.Overwrite);
                    await state.SaveAsync(sourceKey, item, formatKey, ExportItemStatus.Completed, info.Length, null, token);
                    Interlocked.Increment(ref succeeded);
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(CreateProgress(done, total, succeeded, skipped, failed,
                        item.Item.Title, ExportItemStatus.Completed));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    TryDeletePartFile(partPath);
                    throw;
                }
                catch (Exception ex)
                {
                    TryDeletePartFile(partPath);
                    failures.Add(new ExportFailure(item.Item.Title, item.Item.ContentToken, ex.Message));
                    await state.SaveAsync(sourceKey, item, formatKey, ExportItemStatus.Failed, null, ex.Message, token);
                    Interlocked.Increment(ref failed);
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(CreateProgress(done, total, succeeded, skipped, failed,
                        item.Item.Title, ExportItemStatus.Failed, ex.Message));
                }
            });

        var summary = new ExportSummary
        {
            SourceName = sourceName,
            OutputDirectory = sourceDirectory,
            Total = total,
            Succeeded = succeeded,
            Skipped = skipped,
            Unsupported = unsupported,
            Failed = failed
        };
        summary.Failures.AddRange(failures.OrderBy(x => x.Title, StringComparer.CurrentCulture));
        await WriteReportAsync(summary, cancellationToken);
        return summary;
    }

    public async Task<ExportPreparation> PrepareAsync(
        ExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        progress?.Report(CreateProgress(0, 0, 0, 0, 0, "正在读取飞书目录……", ExportItemStatus.Pending));

        string sourceName;
        List<ExportItem> items;
        var scanProgress = new CallbackProgress<DirectoryScanProgress>(scan =>
            progress?.Report(CreateProgress(
                0,
                0,
                0,
                0,
                0,
                $"正在读取飞书目录：已发现 {scan.DiscoveredItems} 个项目，待扫描 {scan.PendingContainers} 个目录",
                ExportItemStatus.Pending)));

        if (options.SourceType == ExportSourceType.Wiki)
        {
            var space = await apiClient.GetWikiSpaceAsync(options.SourceId, cancellationToken);
            sourceName = space.Name;
            items = [.. await apiClient.ListWikiItemsAsync(options.SourceId, scanProgress, cancellationToken)];
        }
        else
        {
            var folder = await apiClient.GetFolderMetaAsync(options.SourceId, cancellationToken);
            sourceName = folder.Name;
            items = [.. await apiClient.ListFolderItemsAsync(options.SourceId, scanProgress, cancellationToken)];
        }

        await ExportStateLayout.SaveSourceAsync(options, sourceName, cancellationToken);

        var failures = new List<ExportFailure>();
        var warnings = new List<string>();
        var navigationCandidates = new List<NavigationPageCandidate>();
        var navigationAnalyses = new List<NavigationPageAnalysis>();
        var documentInspections = new Dictionary<string, DocumentInspection>(StringComparer.Ordinal);

        var includesReader = options.OutputMode is ExportOutputMode.Reader or ExportOutputMode.ReaderAndOffice;
        var includesOffice = options.OutputMode is ExportOutputMode.Office or ExportOutputMode.ReaderAndOffice;

        var wikiParentTokens = includesOffice &&
                               options.SourceType == ExportSourceType.Wiki &&
                               options.TreatWikiParentsAsNavigationFolders
            ? items
                .Where(item => !string.IsNullOrWhiteSpace(item.ParentHierarchyToken))
                .Select(item => item.ParentHierarchyToken!)
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var navigationCandidateTokens = items
            .Where(item =>
                wikiParentTokens.Contains(item.HierarchyToken) &&
                string.Equals(item.Type, "docx", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.HierarchyToken)
            .ToHashSet(StringComparer.Ordinal);

        var documents = items
            .Where(item =>
                string.Equals(item.Type, "docx", StringComparison.OrdinalIgnoreCase) &&
                (includesReader || options.DownloadAttachments || navigationCandidateTokens.Contains(item.HierarchyToken)))
            .ToList();
        var embeddedCount = 0;
        for (var index = 0; index < documents.Count; index++)
        {
            var document = documents[index];
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(CreateProgress(
                0,
                0,
                0,
                0,
                failures.Count,
                $"正在分析文档 {index + 1}/{documents.Count}：{document.Title}",
                ExportItemStatus.Pending,
                includesReader
                    ? $"正在读取页面块；已发现 {embeddedCount} 个内嵌文件"
                    : options.DownloadAttachments
                        ? $"已发现 {embeddedCount} 个内嵌文件"
                        : "正在判断是否为导航页"));

            try
            {
                var childPageTitles = items
                    .Where(item =>
                        string.Equals(item.ParentHierarchyToken, document.HierarchyToken, StringComparison.Ordinal) &&
                        !string.Equals(item.Type, "embedded_file", StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Title)
                    .ToHashSet(StringComparer.Ordinal);
                var inspection = await apiClient.InspectDocumentAsync(
                    document,
                    cancellationToken,
                    childPageTitles);
                documentInspections[document.HierarchyToken] = inspection;
                if (options.DownloadAttachments)
                {
                    items.AddRange(inspection.EmbeddedFiles);
                }
                if (includesReader || options.DownloadAttachments)
                {
                    embeddedCount += inspection.EmbeddedFiles.Count;
                }

                if (navigationCandidateTokens.Contains(document.HierarchyToken))
                {
                    var hierarchyPath = BuildHierarchyPath(document, items);
                    var reason = CreateNavigationReason(inspection.Content);
                    var defaultSkip = inspection.Content.Classification ==
                                      NavigationPageClassification.LikelyNavigation;
                    navigationAnalyses.Add(new NavigationPageAnalysis(
                        document.HierarchyToken,
                        document.Title,
                        hierarchyPath,
                        inspection.Content.Classification,
                        defaultSkip,
                        inspection.Content.MaxConsecutiveBodyBlocks,
                        inspection.Content.BodyCharacterCount,
                        inspection.Content.BlockTypeCounts,
                        inspection.Content.RichBlockTypes,
                        inspection.Content.UnknownBlockTypes,
                        inspection.Content.IgnoredNestedBlockCount,
                        inspection.Content.HasNavigationLikeTable,
                        inspection.Content.HasNavigationAddOnPattern,
                        inspection.Content.AddOnComponentTypeIds,
                        reason,
                        null));

                    if (inspection.Content.Classification != NavigationPageClassification.Substantive)
                    {
                        navigationCandidates.Add(new NavigationPageCandidate(
                            document.HierarchyToken,
                            document.Title,
                            hierarchyPath,
                            inspection.Content.Classification,
                            defaultSkip,
                            inspection.Content.MaxConsecutiveBodyBlocks,
                            inspection.Content.BodyCharacterCount,
                            reason));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var isNavigationCandidate = navigationCandidateTokens.Contains(document.HierarchyToken);
                if (isNavigationCandidate)
                {
                    var hierarchyPath = BuildHierarchyPath(document, items);
                    const string reason = "文档内容分析失败，无法自动确认；默认保留导出";
                    navigationCandidates.Add(new NavigationPageCandidate(
                        document.HierarchyToken,
                        document.Title,
                        hierarchyPath,
                        NavigationPageClassification.Uncertain,
                        false,
                        0,
                        0,
                        reason));
                    navigationAnalyses.Add(new NavigationPageAnalysis(
                        document.HierarchyToken,
                        document.Title,
                        hierarchyPath,
                        NavigationPageClassification.Uncertain,
                        false,
                        0,
                        0,
                        new Dictionary<int, int>(),
                        [],
                        [],
                        0,
                        false,
                        false,
                        [],
                        reason,
                        ex.Message));
                }

                warnings.Add(isNavigationCandidate
                    ? $"{document.Title}：文档内容分析失败，已默认保留并列入待确认项。{ex.Message}"
                    : $"{document.Title}：文档内容或内嵌附件扫描失败。{ex.Message}");
                if (options.DownloadAttachments)
                {
                    var failure = new ExportFailure(
                        $"{document.Title}（内嵌附件扫描）",
                        document.ContentToken,
                        ex.Message);
                    failures.Add(failure);
                    progress?.Report(CreateProgress(
                        0,
                        0,
                        0,
                        0,
                        failures.Count,
                        failure.Title,
                        ExportItemStatus.Failed,
                        ex.Message));
                }
            }
        }

        return new ExportPreparation(
            sourceName,
            items,
            failures,
            warnings,
            navigationCandidates,
            navigationAnalyses,
            documentInspections);
    }

    private static string CreateNavigationReason(DocumentContentAnalysis analysis)
    {
        if (analysis.Classification == NavigationPageClassification.Substantive)
        {
            var reasons = new List<string>();
            if (analysis.MaxConsecutiveBodyBlocks >= 3)
            {
                reasons.Add($"连续正文 {analysis.MaxConsecutiveBodyBlocks} 块");
            }
            if (analysis.BodyCharacterCount >= 100)
            {
                reasons.Add($"正文约 {analysis.BodyCharacterCount} 字");
            }
            if (analysis.RichBlockTypes.Count > 0)
            {
                reasons.Add("包含富内容块 " + string.Join("、", analysis.RichBlockTypes));
            }
            return "判定为有实际内容：" + string.Join("；", reasons);
        }

        if (analysis.Classification == NavigationPageClassification.Uncertain)
        {
            var reasons = new List<string>();
            if (analysis.HasNavigationLikeTable)
            {
                reasons.Add("发现内容均为链接且与标题或子页面对应的表格");
            }
            if (analysis.UnknownBlockTypes.Contains(40))
            {
                var componentTypes = analysis.AddOnComponentTypeIds.Count > 0
                    ? "（组件类型 " + string.Join("、", analysis.AddOnComponentTypeIds) + "）"
                    : string.Empty;
                reasons.Add("发现用途无法确认的新版文档小组件" + componentTypes);
            }
            var otherUnknownTypes = analysis.UnknownBlockTypes.Where(type => type != 40).ToArray();
            if (otherUnknownTypes.Length > 0)
            {
                reasons.Add("发现尚未识别的块类型 " + string.Join("、", otherUnknownTypes));
            }
            reasons.Add("默认保留，请人工确认");
            return string.Join("；", reasons);
        }

        if (analysis.HasNavigationAddOnPattern)
        {
            return "仅发现目录型小组件以及成组对应的标题和子页面目录，未发现实际正文";
        }

        if (analysis.MaxConsecutiveBodyBlocks == 0 && analysis.BodyCharacterCount == 0)
        {
            return "未发现非空正文、表格、图片或附件等实际内容";
        }

        return $"最长连续正文 {analysis.MaxConsecutiveBodyBlocks} 块，正文约 {analysis.BodyCharacterCount} 字，未达到保留条件";
    }

    private static string BuildHierarchyPath(ExportItem document, IReadOnlyList<ExportItem> items)
    {
        var byToken = items
            .Where(item => !string.Equals(item.Type, "embedded_file", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.HierarchyToken, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var segments = new Stack<string>();
        var current = document;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (visited.Add(current.HierarchyToken))
        {
            segments.Push(current.Title);
            if (string.IsNullOrWhiteSpace(current.ParentHierarchyToken) ||
                !byToken.TryGetValue(current.ParentHierarchyToken, out current))
            {
                break;
            }
        }

        return string.Join(" / ", segments);
    }

    private static async Task RetireTrackedNavigationPagesAsync(
        IEnumerable<PlannedExportItem> navigationFolders,
        string sourceKey,
        string sourceDirectory,
        ExportStateStore state,
        IProgress<ExportProgress>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var navigation in navigationFolders)
        {
            var trackedPaths = await state.GetTrackedCompletedPathsAsync(
                sourceKey,
                navigation.Item.HierarchyToken,
                sourceDirectory,
                cancellationToken);
            if (trackedPaths.Count == 0)
            {
                continue;
            }

            foreach (var relativePath in trackedPaths)
            {
                var sourcePath = Path.Combine(sourceDirectory, relativePath);
                var backupPath = Path.Combine(
                    sourceDirectory,
                    ".feishu-export",
                    "retired-navigation-pages",
                    relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                backupPath = GetAvailableBackupPath(backupPath);
                File.Move(sourcePath, backupPath);
            }

            await state.ForgetItemAsync(sourceKey, navigation.Item.HierarchyToken, cancellationToken);
            progress?.Report(CreateProgress(
                0,
                0,
                0,
                0,
                0,
                navigation.Item.Title,
                ExportItemStatus.Skipped,
                "旧版已导出的导航页已移入内部备份目录"));
        }
    }

    private static string GetAvailableBackupPath(string originalPath)
    {
        if (!File.Exists(originalPath))
        {
            return originalPath;
        }

        var directory = Path.GetDirectoryName(originalPath)!;
        var extension = Path.GetExtension(originalPath);
        var stem = Path.GetFileNameWithoutExtension(originalPath);
        var counter = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{stem}（{counter++}）{extension}");
        } while (File.Exists(candidate));
        return candidate;
    }

    private static void ValidateOptions(ExportOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ExportRoot);
        if (options.DocumentFormat is not ("docx" or "pdf"))
        {
            throw new ArgumentOutOfRangeException(nameof(options.DocumentFormat));
        }
        if (options.MaxParallelism is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxParallelism), "并发数量必须在 1 到 4 之间。");
        }
    }

    private static ExportProgress CreateProgress(
        int completed,
        int total,
        int succeeded,
        int skipped,
        int failed,
        string current,
        ExportItemStatus status,
        string? message = null) => new()
    {
        Completed = completed,
        Total = total,
        Succeeded = succeeded,
        Skipped = skipped,
        Failed = failed,
        CurrentItem = current,
        Status = status,
        Message = message
    };

    private static string GetAvailableVersionPath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath)!;
        var extension = Path.GetExtension(originalPath);
        var stem = Path.GetFileNameWithoutExtension(originalPath);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(directory, $"{stem} [{stamp}]{extension}");
        var counter = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{stem} [{stamp}-{counter++}]{extension}");
        }
        return candidate;
    }

    private static void TryDeletePartFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 下一次写入会使用 FileMode.Create 再次尝试覆盖。
        }
        catch (UnauthorizedAccessException)
        {
            // 保留原始异常处理路径，避免清理失败掩盖真正的下载异常。
        }
    }

    private static async Task WriteReportAsync(ExportSummary summary, CancellationToken cancellationToken)
    {
        var path = Path.Combine(summary.OutputDirectory, "export-report.csv");
        var builder = new StringBuilder();
        builder.AppendLine("项目,数值");
        builder.AppendLine($"来源,{Csv(summary.SourceName)}");
        builder.AppendLine($"总数,{summary.Total}");
        builder.AppendLine($"成功,{summary.Succeeded}");
        builder.AppendLine($"跳过,{summary.Skipped}");
        builder.AppendLine($"不支持,{summary.Unsupported}");
        builder.AppendLine($"失败,{summary.Failed}");
        builder.AppendLine();
        builder.AppendLine("失败文档,Token,错误");
        foreach (var failure in summary.Failures)
        {
            builder.AppendLine($"{Csv(failure.Title)},{Csv(failure.Token)},{Csv(failure.Error)}");
        }
        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken);
    }

    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
