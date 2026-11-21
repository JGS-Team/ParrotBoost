using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NLog;

namespace ParrotBoost;

public sealed class UpdateCheckResult
{
    public bool IsUpdateAvailable { get; init; }
    public string LatestVersion { get; init; } = string.Empty;
    public string ReleaseNotes { get; init; } = string.Empty;
    public string? DownloadUrl { get; init; }
    public string ReleaseUrl { get; init; } = "https://github.com/JGS-Team/ParrotBoost/releases";
}

public static class ApplicationUpdateService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    public const string CurrentVersion = "3.0.4";
    public const string CurrentInstallerAppId = "{EC963CDD-2AA6-417B-B547-5D10858624CF}_is1";
    public const string LegacyInstallerAppId = "{90540FF6-ACF6-4C3E-9F86-911586175009}_is1";
    public const string RepositoryOwner = "JGS-Team";
    public const string RepositoryName = "ParrotBoost";
    public const string ReleasesPageUrl = "https://github.com/JGS-Team/ParrotBoost/releases";

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

            string versionClean = tagName.TrimStart('v', 'V').Trim();
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
        if (TryExtractNumericVersion(latestVersionStr, out var latest) &&
            TryExtractNumericVersion(currentVersionStr, out var current))
        {
            return latest > current;
        }

        return false;
    }

    private static bool TryExtractNumericVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var match = Regex.Match(raw, @"\d+(?:\.\d+)+");
        if (match.Success && Version.TryParse(match.Value, out var parsed))
        {
            version = parsed;
            return true;
        }

        return false;
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
                using (var fileStream = new FileStream(tempInstallerPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
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

            var installerInfo = new FileInfo(tempInstallerPath);
            if (!installerInfo.Exists || installerInfo.Length < 1024)
            {
                return false;
            }

            // Launch the new Inno Setup installer FIRST (which handles in-place upgrades and removes legacy installs after UAC approval)
            var startInfo = new ProcessStartInfo
            {
                FileName = tempInstallerPath,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/SP- /VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /NORESTART"
            };

            using var startedProcess = Process.Start(startInfo);
            if (startedProcess == null)
            {
                return false;
            }

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
            string[] appIds = { CurrentInstallerAppId, LegacyInstallerAppId };
            foreach (string appId in appIds)
            {
                string[] registryRoots =
                {
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
                            string exePath = ExtractExecutablePath(uninst);
                            if (File.Exists(exePath))
                            {
                                var psi = new ProcessStartInfo
                                {
                                    FileName = exePath,
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
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "Silent removal of old version encountered a non-critical error.");
        }
    }

    private static string ExtractExecutablePath(string commandLine)
    {
        string trimmed = commandLine.Trim();
        if (trimmed.StartsWith("\"", StringComparison.Ordinal))
        {
            int endQuote = trimmed.IndexOf('\"', 1);
            if (endQuote > 1)
            {
                return trimmed.Substring(1, endQuote - 1);
            }
        }

        int exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            return trimmed.Substring(0, exeIndex + 4).Trim('\"');
        }

        return trimmed.Trim('\"');
    }

    public static string ResolveLocalInstallerPath()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] relativeCandidates =
        {
            Path.Combine(baseDir, "Installer", "ParrotBoost-Setup-x64.exe"),
            Path.Combine(baseDir, "Installer", "ParrotBoost Installer.exe"),
            Path.Combine(baseDir, "Instalator", "ParrotBoost-Setup-x64.exe"),
            Path.Combine(baseDir, "ParrotBoost-Setup-x64.exe"),
            Path.Combine(baseDir, "ParrotBoost Installer.exe")
        };

        foreach (string candidate in relativeCandidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        try
        {
            var dir = new DirectoryInfo(baseDir);
            for (int depth = 0; depth < 4 && dir != null; depth++, dir = dir.Parent)
            {
                string p0 = Path.Combine(dir.FullName, "Installer", "ParrotBoost-Setup-x64.exe");
                if (File.Exists(p0)) return p0;

                string p1 = Path.Combine(dir.FullName, "Installer", "ParrotBoost Installer.exe");
                if (File.Exists(p1)) return p1;

                string p2 = Path.Combine(dir.FullName, "Source", "Instalator", "ParrotBoost-Setup-x64.exe");
                if (File.Exists(p2)) return p2;
            }
        }
        catch
        {
        }

        return relativeCandidates[0];
    }
}
