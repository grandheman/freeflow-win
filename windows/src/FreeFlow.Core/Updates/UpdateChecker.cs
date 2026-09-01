using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FreeFlow.Core.Net;

namespace FreeFlow.Core.Updates;

/// <summary>A published release that is newer than the running build.</summary>
public sealed record AvailableUpdate(
    SemanticVersion Version,
    string TagName,
    string? DownloadUrl,
    string? ReleaseNotes,
    string ReleasePageUrl);

/// <summary>
/// Checks the project's GitHub releases for a newer Windows build.
/// </summary>
/// <remarks>
/// <para>
/// The macOS build in <c>Sources/UpdateManager.swift</c> downloads and swaps in a
/// signed DMG itself. This deliberately stops at telling the user and opening the
/// release page.
/// </para>
/// <para>
/// Silently replacing a running executable on Windows means writing to Program Files,
/// which needs elevation, and an unsigned self-updater that does so is exactly the
/// shape of an attack. Handing off to the browser keeps the trust boundary where the
/// user can see it. A future signed MSIX or Squirrel package can automate this
/// properly.
/// </para>
/// </remarks>
public sealed class UpdateChecker
{
    private readonly HttpClient _client;
    private readonly string _releasesUrl;
    private readonly string _assetSuffix;

    public UpdateChecker(
        string repository = "grandheman/freeflow-win",
        string assetSuffix = ".msi",
        HttpClient? client = null)
    {
        _releasesUrl = $"https://api.github.com/repos/{repository}/releases?per_page=100";
        _assetSuffix = assetSuffix;
        _client = client ?? LlmApiTransport.Client;
    }

    /// <summary>
    /// Returns the newest release above <paramref name="currentVersion"/>, or null.
    /// </summary>
    /// <param name="includePrereleases">
    /// When false, drafts and prereleases are skipped so a test build never prompts
    /// ordinary users.
    /// </param>
    public async Task<AvailableUpdate?> CheckAsync(
        string currentVersion,
        bool includePrereleases = false,
        CancellationToken cancellationToken = default)
    {
        var current = SemanticVersion.TryParse(currentVersion);
        if (current is null) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _releasesUrl);
            // GitHub rejects requests without a User-Agent.
            request.Headers.TryAddWithoutValidation("User-Agent", "FreeFlow-Windows");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await _client.SendAsync(request, timeoutSource.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(timeoutSource.Token).ConfigureAwait(false);
            return SelectNewest(body, current, includePrereleases, _assetSuffix);
        }
        catch (Exception)
        {
            // A failed update check is never worth surfacing as an error; the app
            // works fine without it.
            return null;
        }
    }

    /// <summary>Picks the highest release newer than <paramref name="current"/>.</summary>
    internal static AvailableUpdate? SelectNewest(
        string releasesJson,
        SemanticVersion current,
        bool includePrereleases,
        string assetSuffix)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(releasesJson);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            AvailableUpdate? best = null;

            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (ReadBool(release, "draft")) continue;
                if (!includePrereleases && ReadBool(release, "prerelease")) continue;

                var tagName = ReadString(release, "tag_name");
                var version = SemanticVersion.TryParse(tagName);
                if (version is null || version <= current) continue;
                if (best is not null && version <= best.Version) continue;

                best = new AvailableUpdate(
                    version,
                    tagName ?? version.ToString(),
                    FindAsset(release, assetSuffix),
                    ReadString(release, "body"),
                    ReadString(release, "html_url") ?? "");
            }

            return best;
        }
    }

    private static string? FindAsset(JsonElement release, string assetSuffix)
    {
        if (!release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var url = ReadString(asset, "browser_download_url");
            if (url is not null && url.EndsWith(assetSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
