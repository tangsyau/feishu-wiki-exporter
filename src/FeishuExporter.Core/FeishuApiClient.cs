using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace FeishuExporter.Core;

public sealed class FeishuApiClient : IDisposable
{
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(650);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly FeishuCredentials _credentials;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly SemaphoreSlim _rateLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;
    private bool _disposed;

    public FeishuApiClient(FeishuCredentials credentials, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.AppId);
        ArgumentException.ThrowIfNullOrWhiteSpace(credentials.AppSecret);

        _credentials = credentials;
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _httpClient.BaseAddress = new Uri("https://open.feishu.cn/");
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("FeishuWikiExporter/1.0");
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        _ = await GetAccessTokenAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WikiSpace>> ListWikiSpacesAsync(CancellationToken cancellationToken = default)
    {
        var spaces = new List<WikiSpace>();
        string? pageToken = null;

        do
        {
            var path = "open-apis/wiki/v2/spaces?page_size=50";
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                path += $"&page_token={Uri.EscapeDataString(pageToken)}";
            }

            var page = await GetEnvelopeAsync<PageData<WikiSpace>>(path, cancellationToken);
            spaces.AddRange(page.Items ?? []);
            pageToken = page.HasMore ? page.PageToken ?? page.NextPageToken : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return spaces;
    }

    public async Task<WikiSpace> GetWikiSpaceAsync(string spaceId, CancellationToken cancellationToken = default)
    {
        var data = await GetEnvelopeAsync<WikiSpaceInfoData>(
            $"open-apis/wiki/v2/spaces/{Uri.EscapeDataString(spaceId)}",
            cancellationToken);
        return data.Space;
    }

    public async Task<FolderMeta> GetFolderMetaAsync(string folderToken, CancellationToken cancellationToken = default)
    {
        return await GetEnvelopeAsync<FolderMeta>(
            $"open-apis/drive/explorer/v2/folder/{Uri.EscapeDataString(folderToken)}/meta",
            cancellationToken);
    }

    public async Task<IReadOnlyList<ExportItem>> ListWikiItemsAsync(
        string spaceId,
        IProgress<DirectoryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ExportItem>();
        var parents = new Queue<string?>();
        parents.Enqueue(null);

        while (parents.Count > 0)
        {
            var parent = parents.Dequeue();
            var siblingOrder = 0;
            string? pageToken = null;
            do
            {
                var path = $"open-apis/wiki/v2/spaces/{Uri.EscapeDataString(spaceId)}/nodes?page_size=50";
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    path += $"&parent_node_token={Uri.EscapeDataString(parent)}";
                }
                if (!string.IsNullOrWhiteSpace(pageToken))
                {
                    path += $"&page_token={Uri.EscapeDataString(pageToken)}";
                }

                var page = await GetEnvelopeAsync<PageData<WikiNodeDto>>(path, cancellationToken);
                foreach (var node in page.Items ?? [])
                {
                    result.Add(new ExportItem
                    {
                        HierarchyToken = node.NodeToken,
                        ContentToken = node.ObjectToken,
                        ParentHierarchyToken = node.ParentNodeToken,
                        Title = node.Title,
                        Type = node.ObjectType,
                        ModifiedTime = node.ModifiedTime,
                        SiblingOrder = siblingOrder++,
                        IsFolder = false
                    });

                    if (node.HasChild)
                    {
                        parents.Enqueue(node.NodeToken);
                    }
                }

                progress?.Report(new DirectoryScanProgress(result.Count, parents.Count));

                pageToken = page.HasMore ? page.PageToken ?? page.NextPageToken : null;
            }
            while (!string.IsNullOrWhiteSpace(pageToken));
        }

        return result;
    }

    public async Task<IReadOnlyList<ExportItem>> ListFolderItemsAsync(
        string rootFolderToken,
        IProgress<DirectoryScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ExportItem>();
        var folders = new Queue<string>();
        folders.Enqueue(rootFolderToken);

        while (folders.Count > 0)
        {
            var folderToken = folders.Dequeue();
            string? pageToken = null;
            do
            {
                var path = $"open-apis/drive/v1/files?folder_token={Uri.EscapeDataString(folderToken)}&page_size=50";
                if (!string.IsNullOrWhiteSpace(pageToken))
                {
                    path += $"&page_token={Uri.EscapeDataString(pageToken)}";
                }

                var page = await GetEnvelopeAsync<PageData<CloudFileDto>>(path, cancellationToken);
                foreach (var file in page.Files ?? [])
                {
                    var isFolder = string.Equals(file.Type, "folder", StringComparison.OrdinalIgnoreCase);
                    result.Add(new ExportItem
                    {
                        HierarchyToken = file.Token,
                        ContentToken = file.Token,
                        ParentHierarchyToken = file.ParentToken == rootFolderToken ? null : file.ParentToken,
                        Title = file.Name,
                        Type = file.Type,
                        ModifiedTime = file.ModifiedTime,
                        IsFolder = isFolder
                    });

                    if (isFolder)
                    {
                        folders.Enqueue(file.Token);
                    }
                }

                progress?.Report(new DirectoryScanProgress(result.Count, folders.Count));

                pageToken = page.HasMore ? page.NextPageToken ?? page.PageToken : null;
            }
            while (!string.IsNullOrWhiteSpace(pageToken));
        }

        return result;
    }

    public async Task<IReadOnlyList<ExportItem>> ListDocumentEmbeddedFilesAsync(
        ExportItem document,
        CancellationToken cancellationToken = default)
    {
        var inspection = await InspectDocumentAsync(document, cancellationToken);
        return inspection.EmbeddedFiles;
    }

    public async Task<DocumentInspection> InspectDocumentAsync(
        ExportItem document,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? childPageTitles = null)
    {
        if (!string.Equals(document.Type, "docx", StringComparison.OrdinalIgnoreCase))
        {
            return new DocumentInspection(
                [],
                new DocumentContentAnalysis(
                    0,
                    0,
                    new Dictionary<int, int>(),
                    [],
                    [999],
                    0,
                    false,
                    false,
                    []))
            {
                Blocks = []
            };
        }

        var result = new List<ExportItem>();
        var blocks = new List<DocumentBlockDto>();
        var seenHierarchyTokens = new HashSet<string>(StringComparer.Ordinal);
        string? pageToken = null;
        do
        {
            var path = $"open-apis/docx/v1/documents/{Uri.EscapeDataString(document.ContentToken)}/blocks?page_size=500";
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                path += $"&page_token={Uri.EscapeDataString(pageToken)}";
            }

            var page = await GetEnvelopeAsync<PageData<DocumentBlockDto>>(path, cancellationToken);
            foreach (var block in page.Items ?? [])
            {
                blocks.Add(block);
                var file = block.File;
                var fileToken = file?.Token;
                var sourceFileName = file?.Name;
                if (block.BlockType != 23 || string.IsNullOrWhiteSpace(fileToken))
                {
                    continue;
                }

                var fileName = string.IsNullOrWhiteSpace(sourceFileName)
                    ? $"未命名附件-{block.BlockId}"
                    : sourceFileName;
                var hierarchyToken = $"embedded:{document.HierarchyToken}:{block.BlockId}";
                if (!seenHierarchyTokens.Add(hierarchyToken))
                {
                    continue;
                }

                result.Add(new ExportItem
                {
                    HierarchyToken = hierarchyToken,
                    ContentToken = fileToken,
                    ParentHierarchyToken = document.HierarchyToken,
                    Title = fileName,
                    Type = "embedded_file",
                    ModifiedTime = document.ModifiedTime,
                    IsFolder = false
                });
            }

            pageToken = page.HasMore ? page.PageToken ?? page.NextPageToken : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return new DocumentInspection(result, DocumentContentAnalyzer.Analyze(blocks, childPageTitles))
        {
            Blocks = blocks
        };
    }

    public async Task<string> CreateAndWaitForExportAsync(
        string extension,
        string token,
        string type,
        CancellationToken cancellationToken = default)
    {
        var payload = new { file_extension = extension, token, type };
        var ticket = await PostEnvelopeAsync<ExportTicket>("open-apis/drive/v1/export_tasks", payload, cancellationToken);
        var delays = new[] { 1, 2, 3, 5, 5, 5 };
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        var attempt = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = await GetEnvelopeAsync<ExportTaskData>(
                $"open-apis/drive/v1/export_tasks/{Uri.EscapeDataString(ticket.Ticket)}?token={Uri.EscapeDataString(token)}",
                cancellationToken);

            if (data.Result.JobStatus == 0)
            {
                if (!string.Equals(data.Result.ErrorMessage, "success", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(data.Result.FileToken))
                {
                    throw new FeishuApiException($"导出任务失败：{data.Result.ErrorMessage ?? "未返回文件"}");
                }

                return data.Result.FileToken;
            }

            if (data.Result.JobStatus is not (1 or 2))
            {
                throw new FeishuApiException($"导出任务状态异常：{data.Result.JobStatus}，{data.Result.ErrorMessage}");
            }

            var seconds = delays[Math.Min(attempt++, delays.Length - 1)];
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        }

        throw new TimeoutException("飞书导出任务在 5 分钟内没有完成。");
    }

    public Task DownloadExportAsync(string fileToken, string destinationPath, CancellationToken cancellationToken = default)
    {
        return DownloadAsync(
            $"open-apis/drive/v1/export_tasks/file/{Uri.EscapeDataString(fileToken)}/download",
            destinationPath,
            cancellationToken);
    }

    public Task DownloadFileAsync(string fileToken, string destinationPath, CancellationToken cancellationToken = default)
    {
        return DownloadAsync(
            $"open-apis/drive/v1/files/{Uri.EscapeDataString(fileToken)}/download",
            destinationPath,
            cancellationToken);
    }

    public Task DownloadMediaAsync(string fileToken, string destinationPath, CancellationToken cancellationToken = default)
    {
        return DownloadAsync(
            $"open-apis/drive/v1/medias/{Uri.EscapeDataString(fileToken)}/download",
            destinationPath,
            cancellationToken);
    }

    private async Task<T> GetEnvelopeAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), true, cancellationToken);
        return await ReadEnvelopeAsync<T>(response, cancellationToken);
    }

    private async Task<T> PostEnvelopeAsync<T>(string path, object payload, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload, options: JsonOptions) },
            true,
            cancellationToken);
        return await ReadEnvelopeAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadEnvelopeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(JsonOptions, cancellationToken)
            ?? throw new FeishuApiException("飞书返回了空响应。");
        if (envelope.Code != 0 || envelope.Data is null)
        {
            throw new FeishuApiException($"飞书 API 错误 {envelope.Code}：{envelope.Message}", envelope.Code);
        }

        return envelope.Data;
    }

    private async Task DownloadAsync(string path, string destinationPath, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), true, cancellationToken);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        bool authorize,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await WaitForRateLimitAsync(cancellationToken);
            using var request = requestFactory();
            if (authorize)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancellationToken));
            }

            try
            {
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!IsTransient(response.StatusCode) || attempt == 4)
                {
                    response.Dispose();
                    throw new FeishuApiException($"HTTP {(int)response.StatusCode}：{body}", (int)response.StatusCode);
                }

                var retryDelay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                response.Dispose();
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) &&
                                       !cancellationToken.IsCancellationRequested && attempt < 4)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
        }

        throw new FeishuApiException("请求飞书 API 多次失败。", innerException: lastError);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
        {
            return _accessToken;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_accessToken) && DateTimeOffset.UtcNow < _accessTokenExpiresAt)
            {
                return _accessToken;
            }

            using var response = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "open-apis/auth/v3/tenant_access_token/internal")
                {
                    Content = JsonContent.Create(new { app_id = _credentials.AppId, app_secret = _credentials.AppSecret }, options: JsonOptions)
                },
                false,
                cancellationToken);

            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, cancellationToken)
                ?? throw new FeishuApiException("获取访问令牌时飞书返回了空响应。");
            if (token.Code != 0 || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                throw new FeishuApiException($"获取访问令牌失败 {token.Code}：{token.Message}", token.Code);
            }

            _accessToken = token.AccessToken;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 120));
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
    {
        await _rateLock.WaitAsync(cancellationToken);
        try
        {
            var wait = MinimumRequestInterval - (DateTimeOffset.UtcNow - _lastRequestAt);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }
            _lastRequestAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _rateLock.Release();
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        _tokenLock.Dispose();
        _rateLock.Dispose();
    }
}
