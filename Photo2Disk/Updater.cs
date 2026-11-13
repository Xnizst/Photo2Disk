using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Photo2Disk;

/// <summary>GitHub Releases 上的一個新版本。</summary>
internal sealed record UpdateInfo(Version Version, string Tag, string Notes, string ExeUrl, string ShaUrl, long Size);

/// <summary>
/// 自動更新：啟動時查詢 GitHub Releases 最新版本，使用者同意後下載、核對 SHA-256，
/// 把執行中的 exe 改名成 .old（執行中的檔案不能覆寫但可以改名），再把新版放回原檔名。
/// Release 需附兩個檔案：任意名稱的 .exe，以及內容為該 exe SHA-256 的 .sha256 檔。
/// </summary>
internal static class Updater
{
    /// <summary>GitHub 上的「帳號/repo」。空字串時不檢查更新。</summary>
    public const string Repository = "Xnizst/Photo2Disk";

    private const string UserAgent = "Photo2Disk-Updater";

    /// <summary>測試用：環境變數 PHOTO2DISK_UPDATE_API 可改成本機伺服器的網址。</summary>
    private static string? ApiUrl =>
        Environment.GetEnvironmentVariable("PHOTO2DISK_UPDATE_API")
        ?? (Repository.Length == 0 ? null : $"https://api.github.com/repos/{Repository}/releases/latest");

    public static bool IsConfigured => ApiUrl is not null;

    private static HttpClient MakeClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(UserAgent, typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>有比 current 新的版本時傳回資訊，沒有、沒網路或格式不對時傳回 null（不打擾使用者）。</summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct)
    {
        var url = ApiUrl;
        if (url is null)
        {
            return null;
        }
        try
        {
            using var client = MakeClient(TimeSpan.FromSeconds(10));
            var json = await client.GetStringAsync(url, ct);
            var info = Parse(json);
            return info is not null && info.Version > current ? info : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>解析 GitHub「最新 Release」API 的回應。</summary>
    internal static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()
            || root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
        {
            return null;
        }
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
        {
            return null;
        }
        string? exeUrl = null, shaUrl = null;
        long size = 0;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            var link = asset.GetProperty("browser_download_url").GetString();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && exeUrl is null)
            {
                exeUrl = link;
                size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            }
            else if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) && shaUrl is null)
            {
                shaUrl = link;
            }
        }
        if (exeUrl is null || shaUrl is null)
        {
            return null; // 沒附雜湊檔就不更新，避免下載不完整的檔案
        }
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new UpdateInfo(Normalize(version), tag, notes.Trim(), exeUrl, shaUrl, size);
    }

    /// <summary>1.2 與 1.2.0 視為同一版。</summary>
    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// 下載新版並替換 targetExe。成功後 targetExe 是新版，舊版留在 targetExe.old（下次啟動時刪除）。
    /// 失敗或取消時 targetExe 維持原狀，並刪除下載到一半的檔案。
    /// </summary>
    public static async Task DownloadAndReplaceAsync(UpdateInfo info, string targetExe, IProgress<double>? progress, CancellationToken ct)
    {
        var tmp = targetExe + ".download";
        var old = targetExe + ".old";
        try
        {
            using var client = MakeClient(TimeSpan.FromMinutes(30));
            var shaText = await client.GetStringAsync(info.ShaUrl, ct);
            var expected = ParseSha(shaText) ?? throw new InvalidDataException("雜湊檔格式不正確。");

            using (var response = await client.GetAsync(info.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? info.Size;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    sha.AppendData(buffer, 0, n);
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0)
                    {
                        progress?.Report((double)done / total);
                    }
                }
                var actual = Convert.ToHexString(sha.GetHashAndReset());
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("下載的檔案和雜湊值不符，可能下載不完整，請稍後再試。");
                }
            }

            TryDelete(old);
            File.Move(targetExe, old);
            try
            {
                File.Move(tmp, targetExe);
            }
            catch
            {
                File.Move(old, targetExe); // 放回舊版
                throw;
            }
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>雜湊檔內容取第一段 64 個十六進位字元（相容「雜湊值  檔名」格式）。</summary>
    internal static string? ParseSha(string text)
    {
        var token = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token is { Length: 64 } && token.All(Uri.IsHexDigit) ? token : null;
    }

    /// <summary>刪除上次更新留下的舊版。舊程式剛結束時可能還沒釋放檔案，所以重試幾次。</summary>
    public static void CleanupOldInBackground(string? exe)
    {
        if (exe is null)
        {
            return;
        }
        var old = exe + ".old";
        if (!File.Exists(old) && !File.Exists(exe + ".download"))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 10 && (File.Exists(old) || File.Exists(exe + ".download")); i++)
            {
                TryDelete(old);
                TryDelete(exe + ".download");
                await Task.Delay(1000);
            }
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
