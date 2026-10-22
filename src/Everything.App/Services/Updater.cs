using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Everything.App.Services;

/// <summary>GitHub'daki bir sürüm.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Title,
    string Notes,
    string PageUrl,
    bool IsPrerelease,
    DateTime PublishedUtc,
    string? SetupUrl,
    string? PortableUrl,
    string? ChecksumsUrl)
{
    public string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";
}

/// <summary>
/// GitHub Releases üzerinden güncelleme denetler, indirir ve uygular. Kurulu sürüm sessiz kurulumu
/// çalıştırır; portable sürüm exe'yi yerinde değiştirir (çalışan exe yeniden adlandırılabilir).
/// </summary>
internal static class Updater
{
    private static readonly HttpClient Http = CreateClient();

    public static async Task<ReleaseInfo?> GetLatestAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{AppPaths.RepositoryOwner}/{AppPaths.RepositoryName}/releases?per_page=20";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        ReleaseInfo? best = null;
        foreach (var release in json.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean())
                continue;
            var prerelease = release.GetProperty("prerelease").GetBoolean();
            if (prerelease && !includePrereleases)
                continue;
            var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!TryParseVersion(tag, out var version))
                continue;

            string? setup = null, portable = null, checksums = null;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                var download = asset.GetProperty("browser_download_url").GetString();
                if (name.Contains("setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    setup = download;
                else if (name.Contains("portable", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    portable = download;
                else if (name.Equals("Everything.exe", StringComparison.OrdinalIgnoreCase))
                    portable ??= download;
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase))
                    checksums = download;
            }

            var info = new ReleaseInfo(
                version,
                tag,
                release.GetProperty("name").GetString() ?? tag,
                release.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty,
                release.GetProperty("html_url").GetString() ?? string.Empty,
                prerelease,
                release.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String ? published.GetDateTime() : DateTime.MinValue,
                setup,
                portable,
                checksums);
            if (best is null || info.Version > best.Version)
                best = info;
        }
        return best;
    }

    /// <summary>Mevcut sürümden yeni bir sürüm varsa döndürür.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(bool includePrereleases, CancellationToken cancellationToken)
    {
        var latest = await GetLatestAsync(includePrereleases, cancellationToken);
        return latest is not null && latest.Version > AppPaths.CurrentVersion && DownloadUrl(latest) is not null ? latest : null;
    }

    /// <summary>Bu kurulum türü için uygun dosya (kurulu → Setup, portable → Portable).</summary>
    public static string? DownloadUrl(ReleaseInfo release) =>
        AppPaths.IsInstalled ? release.SetupUrl ?? release.PortableUrl : release.PortableUrl;

    public static async Task<string> DownloadAsync(ReleaseInfo release, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var url = DownloadUrl(release) ?? throw new InvalidOperationException("Bu sürümde uygun dosya yok.");
        Directory.CreateDirectory(AppPaths.UpdateDirectory);
        var fileName = Path.GetFileName(new Uri(url).LocalPath);
        var target = Path.Combine(AppPaths.UpdateDirectory, $"{release.VersionText}-{fileName}");
        var temp = target + ".part";

        using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (total > 0)
                    progress?.Report((double)done / total);
            }
        }

        if (release.ChecksumsUrl is not null)
            await VerifyChecksumAsync(release.ChecksumsUrl, fileName, temp, cancellationToken);

        File.Move(temp, target, overwrite: true);
        return target;
    }

    /// <summary>
    /// İndirilen güncellemeyi uygular; ardından uygulama kapanmalıdır.
    /// <paramref name="beforeRestart"/>: önbelleği kaydetme, tek örnek kilidini bırakma vb.
    /// </summary>
    public static void Apply(string downloadedFile, Action beforeRestart)
    {
        if (AppPaths.IsInstalled)
        {
            beforeRestart();
            Process.Start(new ProcessStartInfo(downloadedFile, "/SILENT /SP- /SUPPRESSMSGBOXES /NOCANCEL /CLOSEAPPLICATIONS /NORESTART")
            {
                UseShellExecute = true,
            });
            return;
        }

        // Portable: çalışan exe'yi kenara al, yenisini yerine koy, yeniden başlat.
        var current = AppPaths.ExePath;
        var old = Path.Combine(AppPaths.ExeDirectory, Path.GetFileNameWithoutExtension(current) + ".old.exe");
        if (File.Exists(old))
            File.Delete(old);
        File.Move(current, old);
        try
        {
            File.Copy(downloadedFile, current, overwrite: true);
        }
        catch
        {
            File.Move(old, current);
            throw;
        }
        beforeRestart();
        Process.Start(new ProcessStartInfo(current, "--updated") { UseShellExecute = true });
    }

    /// <summary>Önceki portable güncellemeden kalan eski exe'yi ve indirilen dosyaları siler.</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(AppPaths.ExeDirectory, "*.old.exe"))
                File.Delete(old);
            if (Directory.Exists(AppPaths.UpdateDirectory))
                Directory.Delete(AppPaths.UpdateDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        var text = tag.TrimStart('v', 'V');
        var dash = text.IndexOf('-');
        if (dash >= 0)
            text = text[..dash];
        if (Version.TryParse(text, out var parsed))
        {
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    private static async Task VerifyChecksumAsync(string checksumsUrl, string fileName, string file, CancellationToken cancellationToken)
    {
        var sums = await Http.GetStringAsync(checksumsUrl, cancellationToken);
        var expected = sums.Split('\n')
            .Select(l => l.Trim().Split(new[] { ' ', '*' }, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2 && p[1].Trim().TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Select(p => p[0])
            .FirstOrDefault();
        if (expected is null)
            return;
        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            stream.Close();
            File.Delete(file);
            throw new InvalidDataException("İndirilen dosyanın SHA-256 özeti eşleşmiyor; güncelleme iptal edildi.");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EverythingArama", AppPaths.CurrentVersionText));
        return client;
    }
}
