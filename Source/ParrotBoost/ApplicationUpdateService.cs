using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using NLog;

namespace ParrotBoost;

public sealed class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; init; }
    public string LatestVersion { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public string? DownloadUrl { get; init; }
    public string ReleaseUrl { get; init; } = "https://github.com/JGS-Parrotnest/ParrotBoost/releases";
}

public static class ApplicationUpdateService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    public const string CurrentVersion = "3.0.3";
    public const string RepositoryOwner = "JGS-Parrotnest";
    public const string RepositoryName = "ParrotBoost";
    public const string ReleasesPageUrl = "https://github.com/JGS-Parrotnest/ParrotBoost/releases";

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    static ApplicationUpdateService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ParrotBoost", CurrentVersion));
        HttpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
    }

    public static async Task<UpdateCheckResult> CheckForUpdatesAsync()
    {
        try
        {
            string apiUrl = $"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest";
            using var response = await HttpClient.GetAsync(apiUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult { IsUpdateAvailable = false };
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tagName = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() ?? ReleasesPageUrl : ReleasesPageUrl;
            string body = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

            string versionClean = tagName.TrimStart('v', 'V');
            if (string.IsNullOrWhiteSpace(versionClean))
            {
                return new UpdateCheckResult { IsUpdateAvailable = false };
            }

            bool isNewer = IsVersionNewer(versionClean, CurrentVersion);
            if (!isNewer)
            {
                return new UpdateCheckResult { IsUpdateAvailable = false, LatestVersion = versionClean };
            }

            string? installerDownloadUrl = null;
            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        if (asset.TryGetProperty("browser_download_url", out var dlProp))
                        {
                            installerDownloadUrl = dlProp.GetString();
                            break;
                        }
                    }
                }
            }

            return new UpdateCheckResult
            {
                IsUpdateAvailable = true,
                LatestVersion = versionClean,
                ReleaseNotes = body,
                DownloadUrl = installerDownloadUrl,
                ReleaseUrl = htmlUrl
            };
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Update check failed.");
            return new UpdateCheckResult { IsUpdateAvailable = false };
        }
    }

    public static bool IsVersionNewer(string latestVersionStr, string currentVersionStr)
    {
        if (Version.TryParse(latestVersionStr, out var latest) && Version.TryParse(currentVersionStr, out var current))
        {
            return latest > current;
        }

        return string.Compare(latestVersionStr, currentVersionStr, StringComparison.OrdinalIgnoreCase) > 0;
    }

    public static async Task<bool> DownloadAndInstallUpdateAsync(string? downloadUrl, IProgress<int>? progress = null)
    {
        try
        {
            string tempInstallerPath = Path.Combine(Path.GetTempPath(), "ParrotBoost-Setup-x64.exe");

            if (!string.IsNullOrWhiteSpace(downloadUrl))
            {
                using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var fileStream = new FileStream(tempInstallerPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                int read;

                while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    totalRead += read;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        int percent = (int)((totalRead * 100) / totalBytes.Value);
                        progress?.Report(percent);
                    }
                }
            }
            else
            {
                string localInstallerCandidate = ResolveLocalInstallerPath();
                if (File.Exists(localInstallerCandidate))
                {
                    File.Copy(localInstallerCandidate, tempInstallerPath, true);
                }
                else
                {
                    Process.Start(new ProcessStartInfo(ReleasesPageUrl) { UseShellExecute = true });
                    return false;
                }
            }

            if (!File.Exists(tempInstallerPath))
            {
                return false;
            }

            SilentlyRemoveOldVersion();

            var startInfo = new ProcessStartInfo
            {
                FileName = tempInstallerPath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/SP- /VERYSILENT /SUPPRESSMSGBOXES /FORCECLOSEAPPLICATIONS /NORESTART"
            };

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to launch installer.");
            return false;
        }
    }

    public static void SilentlyRemoveOldVersion()
    {
        try
        {
            const string appId = "{90540FF6-ACF6-4C3E-9F86-911586175009}_is1";
            string[] registryRoots = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + appId,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\" + appId
            };

            foreach (var root in registryRoots)
            {
                using var hklmKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root);
                using var hkcuKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root);
                var key = hklmKey ?? hkcuKey;
                if (key != null)
                {
                    string? uninst = key.GetValue("QuietUninstallString")?.ToString() 
                                  ?? key.GetValue("UninstallString")?.ToString();
                    if (!string.IsNullOrWhiteSpace(uninst))
                    {
                        uninst = uninst.Trim().Trim('\"');
                        if (File.Exists(uninst))
                        {
                            var psi = new ProcessStartInfo
                            {
                                FileName = uninst,
                                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                                CreateNoWindow = true,
                                UseShellExecute = true,
                                Verb = "runas"
                            };
                            using var p = Process.Start(psi);
                            p?.WaitForExit(8000);
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Silent removal of old version before update encountered a non-critical error.");
        }
    }

    public static string ResolveLocalInstallerPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string candidate1 = Path.Combine(baseDir, "Instalator", "ParrotBoost-Setup-x64.exe");
        if (File.Exists(candidate1)) return candidate1;

        string candidate2 = Path.Combine(baseDir, "ParrotBoost-Setup-x64.exe");
        if (File.Exists(candidate2)) return candidate2;

        try
        {
            var dir = new DirectoryInfo(baseDir);
            while (dir != null)
            {
                string path = Path.Combine(dir.FullName, "Source", "Instalator", "ParrotBoost-Setup-x64.exe");
                if (File.Exists(path)) return path;
                dir = dir.Parent;
            }
        }
        catch
        {
        }

        return candidate1;
    }
}
