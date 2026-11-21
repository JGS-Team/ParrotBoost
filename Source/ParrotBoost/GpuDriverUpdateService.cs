using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using NLog;

namespace ParrotBoost;

internal sealed class GpuDriverUpdateService
{
	public record DriverInfo(
		string GpuName,
		string Manufacturer,
		string RawInstalledVersion,
		string InstalledVersion,
		string? LatestVersion,
		bool UpdateAvailable,
		bool IsLegacy,
		string DownloadUrl,
		bool OnlineCheckSucceeded = true);

	private sealed record GpuHardwareItem(string Name, string Compatibility, string RawVersion, string Manufacturer);

	internal sealed record OnlineLookupResult(string? Version, string? DownloadUrl);

	private static readonly Logger Logger;

	private static readonly HttpClient HttpClient;

	static GpuDriverUpdateService()
	{
		Logger = LogManager.GetCurrentClassLogger();
		HttpClientHandler handler = new HttpClientHandler
		{
			AutomaticDecompression = DecompressionMethods.All,
			AllowAutoRedirect = true
		};
		HttpClient = new HttpClient(handler)
		{
			Timeout = TimeSpan.FromSeconds(10L)
		};
		try
		{
			HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
			HttpClient.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,application/json,*/*;q=0.8");
			HttpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
		}
		catch
		{
		}
	}

	public async Task<List<DriverInfo>> CheckForUpdatesAsync()
	{
		List<DriverInfo> results = new List<DriverInfo>();
		try
		{
			List<GpuHardwareItem> installedGpus = GetInstalledGpus();
			Dictionary<string, OnlineLookupResult> lookupCache = new Dictionary<string, OnlineLookupResult>(StringComparer.OrdinalIgnoreCase);
			foreach (GpuHardwareItem gpu in installedGpus)
			{
				string manufacturer = gpu.Manufacturer;
				string rawVersion = gpu.RawVersion;
				string installedVersion = NormalizeVersion(rawVersion, manufacturer);
				bool isLegacy = IsLegacyGpu(gpu.Name, manufacturer, installedVersion);
				string? latestVersion = null;
				string? directDownloadUrl = null;
				bool updateAvailable = false;
				bool onlineCheckSucceeded = false;
				try
				{
					string cacheKey = $"{manufacturer}|{gpu.Name}|{isLegacy}";
					if (!lookupCache.TryGetValue(cacheKey, out OnlineLookupResult? lookup))
					{
						lookup = await GetLatestVersionAsync(manufacturer, gpu.Name, isLegacy).ConfigureAwait(false);
						lookupCache[cacheKey] = lookup;
					}
					latestVersion = lookup.Version;
					directDownloadUrl = lookup.DownloadUrl;
					if (!string.IsNullOrWhiteSpace(latestVersion) && AreVersionsComparable(installedVersion, latestVersion, manufacturer))
					{
						onlineCheckSucceeded = true;
						updateAvailable = IsNewerVersion(installedVersion, latestVersion);
						if (!updateAvailable && IsNewerVersion(latestVersion, installedVersion))
						{
							latestVersion = installedVersion;
						}
					}
					else if (isLegacy)
					{
						onlineCheckSucceeded = true;
					}
				}
				catch (Exception ex)
				{
					Logger.Warn(ex, $"Failed to check latest driver version for {gpu.Name} ({manufacturer})");
				}
				string downloadUrl = !string.IsNullOrWhiteSpace(directDownloadUrl)
					? directDownloadUrl
					: GetDriverDownloadUrl(manufacturer);
				results.Add(new DriverInfo(
					gpu.Name,
					manufacturer,
					rawVersion,
					installedVersion,
					latestVersion,
					updateAvailable,
					isLegacy,
					downloadUrl,
					onlineCheckSucceeded));
			}
		}
		catch (Exception ex2)
		{
			Logger.Error(ex2, "Critical error during GPU driver update check.");
		}
		return results;
	}

	private static List<GpuHardwareItem> GetInstalledGpus()
	{
		List<GpuHardwareItem> list = new List<GpuHardwareItem>();
		List<GpuHardwareItem> virtualList = new List<GpuHardwareItem>();
		HashSet<string> seenPnp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		try
		{
			using ManagementObjectSearcher val = new ManagementObjectSearcher("SELECT Name, AdapterCompatibility, DriverVersion, PNPDeviceID FROM Win32_VideoController");
			foreach (ManagementObject item in ((IEnumerable)val.Get()).Cast<ManagementObject>())
			{
				string text = ((ManagementBaseObject)item)["Name"]?.ToString()?.Trim() ?? string.Empty;
				string text2 = ((ManagementBaseObject)item)["AdapterCompatibility"]?.ToString()?.Trim() ?? string.Empty;
				string rawVersion = ((ManagementBaseObject)item)["DriverVersion"]?.ToString()?.Trim() ?? "Unknown";
				string pnpId = ((ManagementBaseObject)item)["PNPDeviceID"]?.ToString()?.Trim() ?? string.Empty;
				if (string.IsNullOrWhiteSpace(text))
				{
					continue;
				}
				if (!string.IsNullOrWhiteSpace(pnpId) && !seenPnp.Add(pnpId))
				{
					continue;
				}
				string text3 = DetectManufacturer(text, text2);
				if (text3 == "AMD")
				{
					string? amdVersion = TryGetAmdAdrenalinVersion();
					if (!string.IsNullOrEmpty(amdVersion))
					{
						rawVersion = amdVersion;
					}
				}
				GpuHardwareItem hw = new GpuHardwareItem(text, text2, rawVersion, text3);
				if (MainWindow.IsVirtualGpu(text))
				{
					virtualList.Add(hw);
				}
				else
				{
					list.Add(hw);
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "Failed to enumerate installed GPUs via WMI, falling back to registry.");
		}
		if (list.Count == 0)
		{
			list = GetGpusFromRegistry();
		}
		if (list.Count == 0 && virtualList.Count > 0)
		{
			return virtualList;
		}
		return list;
	}

	private static string DetectManufacturer(string gpuName, string provider)
	{
		if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) || provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
		{
			return "NVIDIA";
		}
		if (gpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) || provider.Contains("AMD", StringComparison.OrdinalIgnoreCase) || provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
		{
			return "AMD";
		}
		if (gpuName.Contains("Intel", StringComparison.OrdinalIgnoreCase) || provider.Contains("Intel", StringComparison.OrdinalIgnoreCase))
		{
			return "Intel";
		}
		return "Unknown";
	}

	private static List<GpuHardwareItem> GetGpusFromRegistry()
	{
		List<GpuHardwareItem> list = new List<GpuHardwareItem>();
		List<GpuHardwareItem> virtualList = new List<GpuHardwareItem>();
		try
		{
			using RegistryKey? registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}");
			if (registryKey != null)
			{
				string[] subKeyNames = registryKey.GetSubKeyNames();
				foreach (string text in subKeyNames)
				{
					if (text.Length != 4 || !char.IsDigit(text[0]))
					{
						continue;
					}
					using RegistryKey? registryKey2 = registryKey.OpenSubKey(text);
					if (registryKey2 == null)
					{
						continue;
					}
					string text2 = registryKey2.GetValue("DriverDesc")?.ToString()?.Trim() ?? string.Empty;
					string text3 = registryKey2.GetValue("ProviderName")?.ToString()?.Trim() ?? string.Empty;
					string rawVersion = registryKey2.GetValue("DriverVersion")?.ToString()?.Trim() ?? string.Empty;
					if (string.IsNullOrWhiteSpace(text2))
					{
						continue;
					}
					string text4 = DetectManufacturer(text2, text3);
					if (text4 == "AMD")
					{
						string? amdVer = ExtractCleanAdrenalinVersion(registryKey2.GetValue("RadeonSoftwareVersion")?.ToString())
							?? ExtractCleanAdrenalinVersion(registryKey2.GetValue("ReleaseVersion")?.ToString())
							?? TryGetAmdAdrenalinVersion();
						if (!string.IsNullOrWhiteSpace(amdVer))
						{
							rawVersion = amdVer;
						}
					}
					GpuHardwareItem hw = new GpuHardwareItem(text2, text3, rawVersion, text4);
					if (MainWindow.IsVirtualGpu(text2))
					{
						virtualList.Add(hw);
					}
					else
					{
						list.Add(hw);
					}
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Warn(ex, "Failed to enumerate GPUs from registry.");
		}
		return (list.Count > 0) ? list : virtualList;
	}

	internal static string? ExtractCleanAdrenalinVersion(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return null;
		}
		Match match = Regex.Match(raw.Trim(), @"^(\d{2}\.\d{1,2}\.\d{1,2})\b");
		if (match.Success)
		{
			return match.Groups[1].Value;
		}
		return null;
	}

	private static string? TryGetAmdAdrenalinVersion()
	{
		try
		{
			using RegistryKey? amdCn = Registry.LocalMachine.OpenSubKey("SOFTWARE\\AMD\\CN");
			string? cnVer = ExtractCleanAdrenalinVersion(amdCn?.GetValue("RadeonSoftwareVersion")?.ToString());
			if (!string.IsNullOrEmpty(cnVer))
			{
				return cnVer;
			}
		}
		catch
		{
		}
		try
		{
			using RegistryKey? registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}");
			if (registryKey != null)
			{
				string[] subKeyNames = registryKey.GetSubKeyNames();
				foreach (string text in subKeyNames)
				{
					if (text.Length != 4 || !char.IsDigit(text[0]))
					{
						continue;
					}
					using RegistryKey? registryKey2 = registryKey.OpenSubKey(text);
					string? rsv = ExtractCleanAdrenalinVersion(registryKey2?.GetValue("RadeonSoftwareVersion")?.ToString());
					if (!string.IsNullOrEmpty(rsv))
					{
						return rsv;
					}
				}
			}
		}
		catch
		{
		}
		return null;
	}

	public static bool IsLegacyGpu(string name, string manufacturer, string normalizedVersion)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return false;
		}
		string text = name.ToUpperInvariant();
		if (text.Contains("NVIDIA") || manufacturer.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
		{
			if (text.Contains("GT 710") || text.Contains("GT710") || text.Contains("GT 720") || text.Contains("GT720") || text.Contains("GT 730") || text.Contains("GT730") || text.Contains("GT 740") || text.Contains("GT740"))
			{
				return true;
			}
			if (text.Contains("GT 610") || text.Contains("GT610") || text.Contains("GT 620") || text.Contains("GT620") || text.Contains("GT 630") || text.Contains("GT630") || text.Contains("GT 640") || text.Contains("GT640") || text.Contains("GT 520") || text.Contains("GT 430") || text.Contains("GT 240") || text.Contains("GT 220") || text.Contains("GT 210") || text.Contains("8400") || text.Contains("8600") || text.Contains("9400") || text.Contains("9500") || text.Contains("9600") || text.Contains("9800") || text.Contains("GTS 250") || text.Contains("GTS 450"))
			{
				return true;
			}
			if (Regex.IsMatch(text, "\\bGTX\\s*(4[0-9]{2}|5[0-9]{2}|6[0-9]{2}|7[6-8][0-9])\\b"))
			{
				return true;
			}
			if (text.Contains("QUADRO FX") || text.Contains("QUADRO 600") || text.Contains("QUADRO 2000") || text.Contains("QUADRO 4000") || text.Contains("QUADRO K600") || text.Contains("QUADRO K2000") || text.Contains("NVS 310") || text.Contains("NVS 315") || text.Contains("NVS 510"))
			{
				return true;
			}
		}
		if (text.Contains("AMD") || text.Contains("RADEON") || manufacturer.Equals("AMD", StringComparison.OrdinalIgnoreCase))
		{
			if (Regex.IsMatch(text, "\\bHD\\s*[2-8][0-9]{3}\\b"))
			{
				return true;
			}
			if (Regex.IsMatch(text, "\\bR[579]\\s*[23][0-9]{2}\\b"))
			{
				return true;
			}
			if (text.Contains("RADEON X") || text.Contains("FIREPRO") || text.Contains("R9 FURY") || text.Contains("R9 NANO"))
			{
				return true;
			}
		}
		if (text.Contains("INTEL") || manufacturer.Equals("Intel", StringComparison.OrdinalIgnoreCase))
		{
			if (Regex.IsMatch(text, "\\b(HD|UHD)\\s*GRAPHICS\\s*[2-5][0-9]{3}\\b"))
			{
				return true;
			}
			if (text.Contains("GRAPHICS MEDIA ACCELERATOR") || text.Contains("GMA "))
			{
				return true;
			}
			if (text == "INTEL(R) HD GRAPHICS" || text == "INTEL HD GRAPHICS")
			{
				return true;
			}
		}
		return false;
	}

	public static string NormalizeVersion(string rawVersion, string manufacturer)
	{
		if (string.IsNullOrWhiteSpace(rawVersion))
		{
			return "Unknown";
		}
		rawVersion = rawVersion.Trim();
		string[] array = rawVersion.Split('.');
		if (array.Length == 2)
		{
			return rawVersion;
		}
		if (manufacturer.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase) && array.Length >= 4)
		{
			string lastPart = array[^1].PadLeft(4, '0');
			string text = array[^2] + lastPart;
			if (text.Length >= 5)
			{
				string text2 = text.Substring(text.Length - 5);
				return text2.Substring(0, 3) + "." + text2.Substring(3);
			}
		}
		return rawVersion;
	}

	internal static bool AreVersionsComparable(string installed, string latest, string manufacturer)
	{
		if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest) || installed.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		if (manufacturer.Equals("AMD", StringComparison.OrdinalIgnoreCase))
		{
			int instParts = installed.Split('.').Length;
			int latParts = latest.Split('.').Length;
			// Do not compare a 4-part Windows WDDM driver number (e.g. 32.0.12011.1036) against a 3-part Adrenalin version (e.g. 26.10.2).
			if (instParts >= 4 && latParts == 3)
			{
				return false;
			}
		}
		return true;
	}

	public static bool IsNewerVersion(string installed, string latest)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
			{
				return false;
			}
			if (installed.Equals(latest, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			if (Version.TryParse(installed, out Version? result) && Version.TryParse(latest, out Version? result2))
			{
				return result2 > result;
			}
			int[] array = (from p in installed.Split('.')
				select int.TryParse(p, out var result3) ? result3 : 0).ToArray();
			int[] array2 = (from p in latest.Split('.')
				select int.TryParse(p, out var result3) ? result3 : 0).ToArray();
			int num = Math.Max(array.Length, array2.Length);
			for (int num2 = 0; num2 < num; num2++)
			{
				int num3 = ((num2 < array.Length) ? array[num2] : 0);
				int num4 = ((num2 < array2.Length) ? array2[num2] : 0);
				if (num4 > num3)
				{
					return true;
				}
				if (num4 < num3)
				{
					return false;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static async Task<OnlineLookupResult> GetLatestVersionAsync(string manufacturer, string gpuName, bool isLegacy)
	{
		string text = gpuName.ToUpperInvariant();
		if (manufacturer.Equals("NVIDIA", StringComparison.OrdinalIgnoreCase))
		{
			if (isLegacy)
			{
				if (Regex.IsMatch(text, "\\bGTX\\s*(4[0-9]{2}|5[0-9]{2})\\b") || text.Contains("GT 520") || text.Contains("GT 430"))
				{
					return new OnlineLookupResult("391.35", null);
				}
				return new OnlineLookupResult("474.92", null);
			}
			return await FetchNvidiaLatestOnlineAsync(text).ConfigureAwait(false);
		}
		if (manufacturer.Equals("AMD", StringComparison.OrdinalIgnoreCase))
		{
			if (isLegacy)
			{
				if (Regex.IsMatch(text, "\\bHD\\s*[2-6][0-9]{3}\\b"))
				{
					return new OnlineLookupResult("15.7.1", null);
				}
				return new OnlineLookupResult("22.6.1", null);
			}
			return new OnlineLookupResult(await FetchAmdLatestOnlineAsync().ConfigureAwait(false), null);
		}
		if (manufacturer.Equals("Intel", StringComparison.OrdinalIgnoreCase))
		{
			if (isLegacy)
			{
				if (text.Contains("2000") || text.Contains("3000"))
				{
					return new OnlineLookupResult("9.17.10.4459", null);
				}
				return new OnlineLookupResult("15.33.53.5161", null);
			}
			return await FetchIntelLatestOnlineDetailedAsync(text).ConfigureAwait(false);
		}
		return new OnlineLookupResult(null, null);
	}

	internal static (int Psid, int Pfid) ResolveNvidiaLookupIds(string upperGpuName)
	{
		bool isLaptop = upperGpuName.Contains("LAPTOP") || upperGpuName.Contains("MOBILE") || upperGpuName.Contains("MAX-Q");
		if (Regex.IsMatch(upperGpuName, "\\bRTX\\s*50[0-9]{2}"))
		{
			return isLaptop ? (Psid: 132, Pfid: 1068) : (Psid: 131, Pfid: 1066);
		}
		if (Regex.IsMatch(upperGpuName, "\\bRTX\\s*40[0-9]{2}"))
		{
			return isLaptop ? (Psid: 129, Pfid: 1007) : (Psid: 127, Pfid: 995);
		}
		if (Regex.IsMatch(upperGpuName, "\\bRTX\\s*30[0-9]{2}"))
		{
			return isLaptop ? (Psid: 122, Pfid: 938) : (Psid: 120, Pfid: 933);
		}
		if (Regex.IsMatch(upperGpuName, "\\bRTX\\s*20[0-9]{2}"))
		{
			return isLaptop ? (Psid: 108, Pfid: 889) : (Psid: 107, Pfid: 882);
		}
		if (Regex.IsMatch(upperGpuName, "\\bGTX\\s*16[0-9]{2}"))
		{
			return isLaptop ? (Psid: 116, Pfid: 900) : (Psid: 115, Pfid: 897);
		}
		if (Regex.IsMatch(upperGpuName, "\\bGTX\\s*10[0-9]{2}"))
		{
			return isLaptop ? (Psid: 102, Pfid: 822) : (Psid: 101, Pfid: 816);
		}
		return isLaptop ? (Psid: 122, Pfid: 938) : (Psid: 120, Pfid: 933);
	}

	internal static string BuildNvidiaLookupUrl(int psid, int pfid, int osId)
	{
		return $"https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup&psid={psid}&pfid={pfid}&osID={osId}&languageCode=1033&beta=0&isWHQL=1&dch=1&sort1=0&dtcid=1&numberOfResults=5";
	}

	internal static (string? Version, string? DownloadUrl) ParseNvidiaDriverLookupJson(string json)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return (null, null);
		}
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (!jsonDocument.RootElement.TryGetProperty("IDS", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0)
			{
				return (null, null);
			}
			string? bestVersion = null;
			string? bestDownloadUrl = null;
			foreach (JsonElement entry in ids.EnumerateArray())
			{
				if (!entry.TryGetProperty("downloadInfo", out JsonElement info) || !info.TryGetProperty("Version", out JsonElement verProp))
				{
					continue;
				}
				string? ver = verProp.GetString()?.Trim();
				if (string.IsNullOrWhiteSpace(ver) || (!Version.TryParse(ver, out Version? _) && !ver.Contains('.')))
				{
					continue;
				}
				if (bestVersion == null || IsNewerVersion(bestVersion, ver))
				{
					bestVersion = ver;
					string? url = null;
					if (info.TryGetProperty("DetailsURL", out JsonElement detailsProp) && !string.IsNullOrWhiteSpace(detailsProp.GetString()))
					{
						url = detailsProp.GetString()!.Trim();
					}
					else if (info.TryGetProperty("DownloadURL", out JsonElement dlProp) && !string.IsNullOrWhiteSpace(dlProp.GetString()))
					{
						url = dlProp.GetString()!.Trim();
					}
					if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
					{
						bestDownloadUrl = url;
					}
				}
			}
			return (bestVersion, bestDownloadUrl);
		}
		catch
		{
			return (null, null);
		}
	}

	private static async Task<OnlineLookupResult> FetchNvidiaLatestOnlineAsync(string upperGpuName)
	{
		bool isWin11 = Environment.OSVersion.Version.Build >= 22000;
		int primaryOs = isWin11 ? 135 : 57;
		int fallbackOs = isWin11 ? 57 : 135;
		(int Psid, int Pfid) primaryIds = ResolveNvidiaLookupIds(upperGpuName);
		bool isLaptop = upperGpuName.Contains("LAPTOP") || upperGpuName.Contains("MOBILE") || upperGpuName.Contains("MAX-Q");
		(int Psid, int Pfid) fallbackIds = isLaptop ? (122, 938) : (120, 933);

		var candidates = new List<(int Psid, int Pfid, int OsId)>
		{
			(primaryIds.Psid, primaryIds.Pfid, primaryOs),
			(primaryIds.Psid, primaryIds.Pfid, fallbackOs)
		};
		if (primaryIds != fallbackIds)
		{
			candidates.Add((fallbackIds.Psid, fallbackIds.Pfid, primaryOs));
			candidates.Add((fallbackIds.Psid, fallbackIds.Pfid, fallbackOs));
		}

		foreach (var (psid, pfid, osId) in candidates)
		{
			try
			{
				string requestUri = BuildNvidiaLookupUrl(psid, pfid, osId);
				using HttpResponseMessage response = await HttpClient.GetAsync(requestUri).ConfigureAwait(continueOnCapturedContext: false);
				if (!response.IsSuccessStatusCode)
				{
					continue;
				}
				string body = await response.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
				var (version, downloadUrl) = ParseNvidiaDriverLookupJson(body);
				if (!string.IsNullOrWhiteSpace(version))
				{
					return new OnlineLookupResult(version, downloadUrl);
				}
			}
			catch (Exception ex)
			{
				Logger.Debug(ex, "Online ping for NVIDIA latest driver failed (psid={0}, pfid={1}, osID={2}).", psid, pfid, osId);
			}
		}
		return new OnlineLookupResult(null, null);
	}

	internal static string? ParseAmdGpuOpenVersionsXml(string xml)
	{
		if (string.IsNullOrWhiteSpace(xml))
		{
			return null;
		}
		string? best = null;
		foreach (Match m in Regex.Matches(xml, @"version\s*=\s*""(\d{2}\.\d{1,2}\.\d{1,2})""", RegexOptions.IgnoreCase))
		{
			string candidate = m.Groups[1].Value.Trim();
			if (best == null || IsNewerVersion(best, candidate))
			{
				best = candidate;
			}
		}
		return best;
	}

	private static async Task<string?> FetchAmdLatestOnlineAsync()
	{
		// 1. Official AMD GPUOpen Vulkan/Adrenalin version mapping feed (reliable HTTPS, no Akamai WAF block).
		try
		{
			using HttpResponseMessage response = await HttpClient.GetAsync("https://raw.githubusercontent.com/GPUOpen-Drivers/amd-vulkan-versions/master/amdversions.xml").ConfigureAwait(continueOnCapturedContext: false);
			if (response.IsSuccessStatusCode)
			{
				string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
				string? version = ParseAmdGpuOpenVersionsXml(xml);
				if (!string.IsNullOrWhiteSpace(version))
				{
					return version;
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "GPUOpen XML feed for AMD latest driver failed.");
		}

		// 2. Fallback: AMD release notes page.
		try
		{
			using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, "https://www.amd.com/en/resources/support-articles/release-notes/RN-RAD-WIN-LATEST.html");
			req.Headers.Referrer = new Uri("https://www.amd.com/");
			using HttpResponseMessage response = await HttpClient.SendAsync(req).ConfigureAwait(continueOnCapturedContext: false);
			if (response.IsSuccessStatusCode)
			{
				string html = await response.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
				Match match = Regex.Match(html, @"Adrenalin\s+Edition\s+(\d{2}\.\d{1,2}\.\d{1,2})", RegexOptions.IgnoreCase);
				if (match.Success)
				{
					return match.Groups[1].Value.Trim();
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Online ping for AMD latest driver failed.");
		}
		return null;
	}

	internal static string? ParseIntelDriverVersionFromHtml(string html)
	{
		if (string.IsNullOrWhiteSpace(html))
		{
			return null;
		}
		string? best = null;
		foreach (Match m in Regex.Matches(html, @"\b(3[1-9]\.0\.\d{3}\.\d{4,5})\b"))
		{
			string candidate = m.Groups[1].Value.Trim();
			if (best == null || IsNewerVersion(best, candidate))
			{
				best = candidate;
			}
		}
		return best;
	}

	internal static OnlineLookupResult ParseIntelDsaSoftwareConfigurationsJson(string json, string? upperGpuName = null)
	{
		if (string.IsNullOrWhiteSpace(json))
		{
			return new OnlineLookupResult(null, null);
		}

		try
		{
			using JsonDocument doc = JsonDocument.Parse(json);
			if (doc.RootElement.ValueKind != JsonValueKind.Array)
			{
				return new OnlineLookupResult(null, null);
			}

			var candidates = new List<(string Name, string Version, string? DownloadUrl, int Id)>();
			foreach (JsonElement item in doc.RootElement.EnumerateArray())
			{
				if (item.TryGetProperty("IsBeta", out JsonElement betaEl) && betaEl.ValueKind == JsonValueKind.True)
				{
					continue;
				}
				string name = item.TryGetProperty("Name", out JsonElement nameEl) ? (nameEl.GetString() ?? string.Empty) : string.Empty;
				if (string.IsNullOrWhiteSpace(name)
					|| !name.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
					|| name.Contains("Historical", StringComparison.OrdinalIgnoreCase)
					|| name.Contains("NPU", StringComparison.OrdinalIgnoreCase)
					|| name.Contains("Media Accelerator", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string rawVer = item.TryGetProperty("Version", out JsonElement verEl) ? (verEl.GetString() ?? string.Empty) : string.Empty;
				Match verMatch = Regex.Match(rawVer, @"\b(3[1-9]\.0\.10[01]\.\d{4,5})\b");
				if (!verMatch.Success)
				{
					continue;
				}
				string cleanVer = verMatch.Groups[1].Value;

				string? url = null;
				if (item.TryGetProperty("Files", out JsonElement filesEl) && filesEl.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement fileEl in filesEl.EnumerateArray())
					{
						if (fileEl.TryGetProperty("Url", out JsonElement urlEl))
						{
							string? rawUrl = urlEl.GetString();
							if (!string.IsNullOrWhiteSpace(rawUrl) && rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
							{
								url = rawUrl;
								break;
							}
						}
					}
				}
				int id = item.TryGetProperty("Id", out JsonElement idEl) && idEl.TryGetInt32(out int parsedId) ? parsedId : 0;
				candidates.Add((name, cleanVer, url, id));
			}

			if (candidates.Count == 0)
			{
				return new OnlineLookupResult(null, null);
			}

			string gpu = upperGpuName?.ToUpperInvariant() ?? string.Empty;
			Func<(string Name, string Version, string? DownloadUrl, int Id), bool> matcher;

			if (gpu.Contains("ARC") && gpu.Contains("PRO"))
			{
				matcher = c => c.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) && c.Name.Contains("Pro", StringComparison.OrdinalIgnoreCase);
			}
			else if (Regex.IsMatch(gpu, @"\b(UHD\s*(GRAPHICS\s*)?6\d\d|HD\s*(GRAPHICS\s*)?[56]\d\d|IRIS\s*(PLUS|PRO)|P[56]30)\b"))
			{
				matcher = c => c.Name.Contains("7th", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("10th", StringComparison.OrdinalIgnoreCase);
			}
			else if (!gpu.Contains("ARC") && (gpu.Contains("IRIS") || gpu.Contains("UHD")))
			{
				matcher = c => c.Name.Contains("11th", StringComparison.OrdinalIgnoreCase) || c.Name.Contains("14th", StringComparison.OrdinalIgnoreCase);
			}
			else
			{
				matcher = c => c.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase) && !c.Name.Contains("Pro", StringComparison.OrdinalIgnoreCase);
			}

			var matched = candidates.Where(matcher).ToList();
			if (matched.Count == 0)
			{
				matched = candidates;
			}

			var best = matched[0];
			for (int i = 1; i < matched.Count; i++)
			{
				if (IsNewerVersion(best.Version, matched[i].Version))
				{
					best = matched[i];
				}
			}

			string? resolvedUrl = best.DownloadUrl ?? (best.Id > 0 ? $"https://www.intel.com/content/www/us/en/download/{best.Id}/" : null);
			return new OnlineLookupResult(best.Version, resolvedUrl);
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Failed to parse Intel DSA software-configurations.json.");
			return new OnlineLookupResult(null, null);
		}
	}

	private static async Task<string?> FetchIntelLatestOnlineAsync()
	{
		OnlineLookupResult res = await FetchIntelLatestOnlineDetailedAsync(null).ConfigureAwait(false);
		return res.Version;
	}

	private static async Task<OnlineLookupResult> FetchIntelLatestOnlineDetailedAsync(string? upperGpuName)
	{
		// 1. Official Intel DSA public data catalog ZIP (HTTP 200 JSON, avoids www.intel.com Akamai WAF 403)
		try
		{
			using HttpResponseMessage response = await HttpClient.GetAsync("https://dsadata.intel.com/data/en").ConfigureAwait(continueOnCapturedContext: false);
			if (response.IsSuccessStatusCode)
			{
				byte[] zipBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(continueOnCapturedContext: false);
				using var ms = new MemoryStream(zipBytes, writable: false);
				using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
				ZipArchiveEntry? entry = zip.GetEntry("software-configurations.json");
				if (entry != null)
				{
					using Stream entryStream = entry.Open();
					using var reader = new StreamReader(entryStream);
					string json = await reader.ReadToEndAsync().ConfigureAwait(continueOnCapturedContext: false);
					OnlineLookupResult dsaResult = ParseIntelDsaSoftwareConfigurationsJson(json, upperGpuName);
					if (!string.IsNullOrWhiteSpace(dsaResult.Version))
					{
						return dsaResult;
					}
				}
			}
		}
		catch (Exception ex)
		{
			Logger.Debug(ex, "Intel DSA catalog lookup failed.");
		}

		// 2. Fallback: Intel download pages
		string[] urls = new string[2]
		{
			"https://www.intel.com/content/www/us/en/download/785597/intel-arc-graphics-windows-driver.html",
			"https://www.intel.com/content/www/us/en/download/785597/intel-arc-iris-xe-graphics-windows.html"
		};
		foreach (string requestUri in urls)
		{
			try
			{
				using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, requestUri);
				req.Headers.Referrer = new Uri("https://www.intel.com/");
				using HttpResponseMessage response = await HttpClient.SendAsync(req).ConfigureAwait(continueOnCapturedContext: false);
				if (response.IsSuccessStatusCode)
				{
					string html = await response.Content.ReadAsStringAsync().ConfigureAwait(continueOnCapturedContext: false);
					string? version = ParseIntelDriverVersionFromHtml(html);
					if (!string.IsNullOrWhiteSpace(version))
					{
						return new OnlineLookupResult(version, requestUri);
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Debug(ex, "Online ping for Intel latest driver failed ({0}).", requestUri);
			}
		}
		return new OnlineLookupResult(null, null);
	}

	public static string GetDriverDownloadUrl(string manufacturer)
	{
		return manufacturer switch
		{
			"NVIDIA" => "https://www.nvidia.com/Download/index.aspx",
			"AMD" => "https://www.amd.com/en/support",
			"Intel" => "https://www.intel.com/content/www/us/en/support/detect.html",
			_ => "https://www.google.com/search?q=graphics+drivers+update",
		};
	}
}
