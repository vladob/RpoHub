using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RpoHub.Application;

namespace RpoHub.Infrastructure;

public sealed class RpoApiClient(HttpClient httpClient) : IRpoApiClient
{
    public async Task<IReadOnlyList<RpoSearchHit>> SearchByIcoAsync(string ico, CancellationToken cancellationToken)
    {
        var normalized = new string(ico.Where(char.IsDigit).ToArray());
        if (normalized.Length is < 6 or > 8) throw new ArgumentException("IČO must contain 6 to 8 digits.", nameof(ico));

        using var response = await httpClient.GetAsync($"search?identifier={Uri.EscapeDataString(normalized)}", cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        var root = document.RootElement;
        var array = root.ValueKind == JsonValueKind.Array
            ? root
            : FindFirstArray(root, "results", "items", "organizations");
        if (array.ValueKind != JsonValueKind.Array) return [];

        return array.EnumerateArray().Select(item => new RpoSearchHit(
            TryInt64(item, "id"), TryString(item, "identifier", "ico"), TryString(item, "fullName", "name"), item.Clone())).ToArray();
    }

    public async Task<JsonDocument> GetEntityAsync(long entityId, bool includeHistory, bool includeUnits, CancellationToken cancellationToken)
    {
        var path = $"entity/{entityId}?showHistoricalData={includeHistory.ToString().ToLowerInvariant()}&showOrganizationUnits={includeUnits.ToString().ToLowerInvariant()}";
        using var response = await httpClient.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private static JsonElement FindFirstArray(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array) return value;
        return default;
    }

    private static long? TryInt64(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;

    private static string? TryString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }
}

public sealed class RpoExportCatalog(HttpClient httpClient, IOptions<RpoOptions> options) : IRpoExportCatalog
{
    public Task<IReadOnlyList<RemoteFile>> ListInitializationFilesAsync(CancellationToken cancellationToken) =>
        ListAsync(options.Value.InitializationPrefix, key =>
            key.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith("_list.txt", StringComparison.OrdinalIgnoreCase), cancellationToken);

    public Task<IReadOnlyList<RemoteFile>> ListDailyFilesAsync(CancellationToken cancellationToken) =>
        ListAsync(options.Value.DailyPrefix, key => key.EndsWith(".json.gz", StringComparison.OrdinalIgnoreCase), cancellationToken);

    private async Task<IReadOnlyList<RemoteFile>> ListAsync(string prefix, Func<string, bool> include, CancellationToken cancellationToken)
    {
        var request = $"?list-type=2&prefix={Uri.EscapeDataString(prefix)}";
        using var response = await httpClient.GetAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var xml = XDocument.Load(await response.Content.ReadAsStreamAsync(cancellationToken));
        var ns = xml.Root?.Name.Namespace ?? XNamespace.None;
        return xml.Descendants(ns + "Contents").Select(node =>
        {
            var key = (string?)node.Element(ns + "Key") ?? string.Empty;
            long? size = long.TryParse((string?)node.Element(ns + "Size"), out var parsedSize) ? parsedSize : null;
            DateTimeOffset? modified = DateTimeOffset.TryParse((string?)node.Element(ns + "LastModified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate) ? parsedDate : null;
            return new RemoteFile(key, new Uri(options.Value.ExportBaseUrl, key), size, modified, (string?)node.Element(ns + "ETag"));
        }).Where(file => include(file.Key)).ToArray();
    }
}
