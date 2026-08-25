using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexUsageMonitor.Core.Models;

namespace CodexUsageMonitor.Core.Services;

public sealed partial class PriceCatalogService : IPriceCatalogService
{
    public static readonly Uri OfficialPricingUri = new("https://developers.openai.com/api/docs/models");
    public const int MaxOfficialResponseBytes = 2 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly string _cachePath;
    private PricingSnapshot? _sessionSnapshot;

    public PriceCatalogService(HttpClient httpClient, string cachePath)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(25);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CodexUsageMonitor/0.1");
        _cachePath = cachePath;
    }

    public static HttpClient CreateDefaultHttpClient()
        => new(new HttpClientHandler { AllowAutoRedirect = false });

    public async Task<PricingSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_sessionSnapshot is not null)
        {
            return _sessionSnapshot;
        }

        IReadOnlyDictionary<string, TokenPrice> parsed = new Dictionary<string, TokenPrice>();
        try
        {
            var results = await Task.WhenAll(ModelDefinitions.Select(model => ReadOfficialPriceAsync(model, cancellationToken)));
            parsed = results
                .Where(item => item is not null)
                .Cast<TokenPrice>()
                .ToDictionary(item => item.Model, StringComparer.OrdinalIgnoreCase);
            if (parsed.Count == ModelDefinitions.Length)
            {
                var snapshot = new PricingSnapshot(parsed, DateTimeOffset.UtcNow, OfficialPricingUri, true, null);
                await SaveCacheAsync(snapshot, cancellationToken);
                return _sessionSnapshot = snapshot;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // 网络和页面结构变化均回退到最后成功快照或内置价格。
        }

        var cached = await LoadCacheAsync(cancellationToken);
        var baseline = cached ?? CreateBuiltInSnapshot();
        if (parsed.Count == 0)
        {
            return _sessionSnapshot = baseline;
        }

        var merged = baseline.Prices.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parsed)
        {
            merged[pair.Key] = pair.Value;
        }

        return _sessionSnapshot = new PricingSnapshot(
            merged,
            baseline.UpdatedAt,
            OfficialPricingUri,
            false,
            $"官方价格仅成功读取 {parsed.Count}/{ModelDefinitions.Length} 个模型；未覆盖完整缓存");
    }

    private async Task<TokenPrice?> ReadOfficialPriceAsync(
        string model,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri($"{OfficialPricingUri.AbsoluteUri.TrimEnd('/')}/{model}.md");
            EnsureOfficialUri(uri);
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            EnsureOfficialUri(response.RequestMessage?.RequestUri ?? uri);
            response.EnsureSuccessStatusCode();
            var html = await ReadBoundedUtf8Async(response.Content, cancellationToken);
            return ParseModelPricingHtml(model, html);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static TokenPrice? ParseModelPricingHtml(string model, string html)
    {
        var text = WebUtility.HtmlDecode(TagRegex().Replace(html, " "));
        text = WhitespaceRegex().Replace(text, " ");
        var match = Regex.Match(
            text,
            "Text tokens.{0,300}?Per\\s*1M\\s*tokens.{0,300}?Input\\s*\\$(?<input>[0-9.]+).{0,160}?Cached input\\s*\\$(?<cached>[0-9.]+).{0,160}?Output\\s*\\$(?<output>[0-9.]+)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
        {
            match = Regex.Match(
                text,
                "Text tokens.{0,500}?\\|\\s*Input\\s*\\|\\s*\\$(?<input>[0-9.]+)\\s*\\|\\s*1M tokens\\s*\\|.{0,200}?\\|\\s*Cached input\\s*\\|\\s*\\$(?<cached>[0-9.]+)\\s*\\|\\s*1M tokens\\s*\\|.{0,200}?\\|\\s*Output\\s*\\|\\s*\\$(?<output>[0-9.]+)\\s*\\|\\s*1M tokens\\s*\\|",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        if (!match.Success
            || !TryReadDecimal(match, "input", out var input)
            || !TryReadDecimal(match, "cached", out var cached)
            || !TryReadDecimal(match, "output", out var output))
        {
            return null;
        }

        var slug = UsageCalculator.NormalizeModel(model);
        return new TokenPrice(slug, input, cached, output);
    }

    private async Task SaveCacheAsync(PricingSnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_cachePath)!;
        Directory.CreateDirectory(directory);
        var rows = snapshot.Prices.Values.OrderBy(item => item.Model).ToArray();
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_cachePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, new CacheDocument(snapshot.UpdatedAt, rows), cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // 原子替换成功后临时文件已经不存在；清理失败留给系统临时维护。
            }
        }
    }

    internal static async Task<string> ReadBoundedUtf8Async(
        HttpContent content,
        CancellationToken cancellationToken = default)
    {
        if (content.Headers.ContentLength is > MaxOfficialResponseBytes)
        {
            throw new InvalidDataException($"官方价格响应超过 {MaxOfficialResponseBytes} 字节上限");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min((int)(content.Headers.ContentLength ?? 16 * 1024), MaxOfficialResponseBytes));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaxOfficialResponseBytes)
            {
                throw new InvalidDataException($"官方价格响应超过 {MaxOfficialResponseBytes} 字节上限");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private async Task<PricingSnapshot?> LoadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(_cachePath);
            var document = await JsonSerializer.DeserializeAsync<CacheDocument>(stream, cancellationToken: cancellationToken);
            if (document?.Prices is not { Length: > 0 })
            {
                return null;
            }

            var prices = document.Prices.ToDictionary(item => item.Model, StringComparer.OrdinalIgnoreCase);
            return new PricingSnapshot(prices, document.UpdatedAt, OfficialPricingUri, false, "官方价格刷新失败，使用最近成功快照");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static PricingSnapshot CreateBuiltInSnapshot(string? statusMessage = null)
    {
        var prices = new[]
        {
            new TokenPrice("gpt-5.6-sol", 4m, 0.4m, 20m),
            new TokenPrice("gpt-5.6-terra", 2m, 0.2m, 12m),
            new TokenPrice("gpt-5.6-luna", 0.2m, 0.02m, 1.2m),
            new TokenPrice("gpt-5.5", 5m, 0.5m, 30m),
            new TokenPrice("gpt-5.4", 2.5m, 0.25m, 15m),
            new TokenPrice("gpt-5.4-mini", 0.75m, 0.075m, 4.5m)
        }.ToDictionary(item => item.Model, StringComparer.OrdinalIgnoreCase);

        return new PricingSnapshot(
            prices,
            new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero),
            OfficialPricingUri,
            false,
            statusMessage ?? "官方价格刷新失败，使用 2026-08-24 内置价格");
    }

    private static void EnsureOfficialUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("developers.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("价格请求被限制在 OpenAI 官方开发者文档域名");
        }
    }

    private static readonly string[] ModelDefinitions =
    {
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5.6-luna",
        "gpt-5.5",
        "gpt-5.4",
        "gpt-5.4-mini"
    };

    private sealed record CacheDocument(DateTimeOffset UpdatedAt, TokenPrice[] Prices);

    private static bool TryReadDecimal(Match match, string groupName, out decimal value)
        => decimal.TryParse(
            match.Groups[groupName].Value,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out value);

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
