using System.IO.Compression;
using System.Text.Json.Nodes;

namespace FetchDependencies;

public class FetchDependencies
{
    private const string ReleaseMarkerFileName = "FFXIV_ACT_Plugin.release";
    private const string ReleaseApiUrlGlobal = "https://api.github.com/repos/ravahn/FFXIV_ACT_Plugin/releases/latest";
    private const string ReleaseApiUrlChinese = "https://api.github.com/repos/NewMoe-Technology/FFXIV_ACT_Plugin_CN/releases/latest";

    private Version PluginVersion { get; }
    private string DependenciesDir { get; }
    private bool IsChinese { get; }
    private HttpClient HttpClient { get; }

    private sealed record PluginRelease(string Source, string TagName, string DownloadUrl)
    {
        public string Marker => $"{Source}@{TagName}";
    }

    public FetchDependencies(Version version, string assemblyDir, bool isChinese, HttpClient httpClient)
    {
        PluginVersion = version;
        DependenciesDir = assemblyDir;
        IsChinese = isChinese;
        HttpClient = httpClient;
    }

    public void GetFfxivPlugin()
    {
        var pluginZipPath = Path.Combine(DependenciesDir, "FFXIV_ACT_Plugin.zip");
        var pluginPath = Path.Combine(DependenciesDir, "FFXIV_ACT_Plugin.dll");
        var releaseMarkerPath = Path.Combine(DependenciesDir, ReleaseMarkerFileName);
        PluginRelease release;
        try
        {
            release = GetLatestPluginRelease();
        }
        catch when (File.Exists(pluginPath))
        {
            return;
        }
        
        if (!NeedsUpdate(pluginPath, releaseMarkerPath, release))
            return;
        
        try
        {
            DownloadPlugin(release, pluginZipPath);
            ZipFile.ExtractToDirectory(pluginZipPath, DependenciesDir, true);
        }
        catch (InvalidDataException)
        {
            File.Delete(pluginZipPath);
            DownloadPlugin(release, pluginZipPath);
            ZipFile.ExtractToDirectory(pluginZipPath, DependenciesDir, true);
        }
        File.Delete(pluginZipPath);

        foreach (var deucalionDll in Directory.GetFiles(DependenciesDir, "deucalion*.dll"))
            File.Delete(deucalionDll);

        var patcher = new Patcher(PluginVersion, DependenciesDir);
        patcher.MainPlugin();
        patcher.LogFilePlugin();
        patcher.MemoryPlugin();
        File.WriteAllText(releaseMarkerPath, release.Marker);
    }

    private static bool NeedsUpdate(string dllPath, string releaseMarkerPath, PluginRelease release)
    {
        if (!File.Exists(dllPath) || !File.Exists(releaseMarkerPath))
            return true;

        try
        {
            var installedMarker = File.ReadAllText(releaseMarkerPath).Trim();
            if (!string.Equals(installedMarker, release.Marker, StringComparison.Ordinal))
                return true;

            using var plugin = new TargetAssembly(dllPath);
            return !plugin.ApiVersionMatches();
        }
        catch
        {
            return true;
        }
    }

    private PluginRelease GetLatestPluginRelease()
    {
        var releaseApiUrl = IsChinese ? ReleaseApiUrlChinese : ReleaseApiUrlGlobal;
        using var request = new HttpRequestMessage(HttpMethod.Get, releaseApiUrl);
        request.Headers.UserAgent.ParseAdd("IINACT/1.0");
        using var response = HttpClient.Send(request);
        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream();
        var release = JsonNode.Parse(stream);
        var tagName = release?["tag_name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(tagName))
            throw new Exception("GitHub release response did not contain a tag name.");

        var assets = release?["assets"]?.AsArray()
                     ?? throw new Exception("GitHub release response did not contain any assets.");
        var archiveName = IsChinese ? "FFXIV_ACT_Plugin.zip" : null;
        var asset = assets.SingleOrDefault(node =>
        {
            var name = node?["name"]?.GetValue<string>();
            return IsChinese
                ? string.Equals(name, archiveName, StringComparison.OrdinalIgnoreCase)
                : name is not null &&
                  name.StartsWith("FFXIV_ACT_Plugin", StringComparison.OrdinalIgnoreCase) &&
                  name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                  !name.Contains("sdk", StringComparison.OrdinalIgnoreCase) &&
                  !name.Contains("cafe", StringComparison.OrdinalIgnoreCase);
        });
        var downloadUrl = asset?["browser_download_url"]?.GetValue<string>();

        if (string.IsNullOrEmpty(downloadUrl))
            throw new Exception("Could not find the FFXIV_ACT_Plugin ZIP asset in the latest GitHub release.");

        return new PluginRelease(releaseApiUrl, tagName, downloadUrl);
    }

    private void DownloadPlugin(PluginRelease release, string pluginZipPath)
    {
        DownloadFile(release.DownloadUrl, pluginZipPath);
    }

    private void DownloadFile(string url, string path)
    {
        using var cancelAfterDelay = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var temporaryPath = path + ".download";
        try
        {
            using var response = HttpClient.GetAsync(url, cancelAfterDelay.Token).Result;
            response.EnsureSuccessStatusCode();
            using var downloadStream = response.Content.ReadAsStream(cancelAfterDelay.Token);
            using (var zipFileStream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                downloadStream.CopyTo(zipFileStream);
            }
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
