using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Photo2Disk.TestSupport;

/// <summary>
/// 測試用的本機 HTTP 伺服器，模擬 GitHub「最新 Release」API 與檔案下載。
/// 用 PHOTO2DISK_UPDATE_API 指向 ApiUrl。
/// </summary>
internal sealed class FakeReleaseServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Dictionary<string, byte[]> files = new();
    private string releaseJson = "{}";

    public FakeReleaseServer()
    {
        var port = FreePort();
        BaseUrl = $"http://localhost:{port}/";
        listener.Prefixes.Add(BaseUrl);
        listener.Start();
        _ = Task.Run(Serve);
    }

    public string BaseUrl { get; }

    public string ApiUrl => BaseUrl + "repos/test/test/releases/latest";

    /// <summary>設定最新版。shaOverride 可故意給錯的雜湊；withSha=false 時不附雜湊檔。</summary>
    public void SetRelease(string tag, byte[] exe, string notes = "", string? shaOverride = null, bool withSha = true, bool prerelease = false)
    {
        files["Photo2Disk.exe"] = exe;
        var sha = shaOverride ?? Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant();
        files["Photo2Disk.exe.sha256"] = Encoding.UTF8.GetBytes(sha + "  Photo2Disk.exe\n");
        var assets = new List<object>
        {
            new { name = "Photo2Disk.exe", size = exe.LongLength, browser_download_url = BaseUrl + "dl/Photo2Disk.exe" },
        };
        if (withSha)
        {
            assets.Add(new { name = "Photo2Disk.exe.sha256", size = 80, browser_download_url = BaseUrl + "dl/Photo2Disk.exe.sha256" });
        }
        releaseJson = JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease, body = notes, assets });
    }

    private async Task Serve()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            var path = ctx.Request.Url!.AbsolutePath;
            byte[]? body = null;
            if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                body = Encoding.UTF8.GetBytes(releaseJson);
                ctx.Response.ContentType = "application/json";
            }
            else if (path.StartsWith("/dl/", StringComparison.Ordinal) && files.TryGetValue(path[4..], out var data))
            {
                body = data;
            }
            if (body is null)
            {
                ctx.Response.StatusCode = 404;
            }
            else
            {
                ctx.Response.ContentLength64 = body.LongLength;
                try
                {
                    await ctx.Response.OutputStream.WriteAsync(body);
                }
                catch (Exception ex) when (ex is HttpListenerException or IOException)
                {
                }
            }
            ctx.Response.Close();
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public void Dispose()
    {
        if (!listener.IsListening)
        {
            return;
        }
        listener.Stop();
        listener.Close();
    }
}
