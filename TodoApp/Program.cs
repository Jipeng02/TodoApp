using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

public class Program
{
    private static readonly TimeSpan FetchWindow = TimeSpan.FromHours(24);
    private static readonly int MaxItemsToShow = 12;
    private const int TelegramMaxMessageLength = 3500;

    private static readonly string[] MajorKeywords =
    [
        "announce",
        "announces",
        "announced",
        "launch",
        "launches",
        "release",
        "releases",
        "released",
        "introduce",
        "introduces",
        "breakthrough",
        "funding",
        "raises",
        "acquire",
        "acquires",
        "acquisition",
        "merger",
        "policy",
        "regulation",
        "law",
        "lawsuit",
        "ban",
        "security",
        "vulnerability",
        "breach",
        "model",
        "gpt",
        "claude",
        "gemini",
        "llama",
        "openai",
        "anthropic",
        "deepmind",
        "meta",
        "microsoft",
        "nvidia"
    ];

    private static readonly FeedSource[] Sources =
    [
        new("Planet AI (aggregated)", "https://planet-ai.net/rss.xml"),
        new("NVIDIA Press Room", "https://nvidianews.nvidia.com/releases.xml"),
        new("NVIDIA Blog", "https://feeds.feedburner.com/nvidiablog"),
        new("NVIDIA Developer Blog", "https://developer.nvidia.com/blog/feed"),
        new("r/MachineLearning", "https://www.reddit.com/r/MachineLearning/.rss"),
        new("r/OpenAI", "https://www.reddit.com/r/OpenAI/.rss"),
        new("r/artificial", "https://www.reddit.com/r/artificial/.rss"),
        new("r/LocalLLaMA", "https://www.reddit.com/r/LocalLLaMA/.rss"),
        new("r/singularity", "https://www.reddit.com/r/singularity/.rss"),
        new("r/ChatGPT", "https://www.reddit.com/r/ChatGPT/.rss")
    ];

    public static async Task Main(string[] args)
    {
        var botToken = GetEnvironmentValue("TELEGRAM_BOT_TOKEN", "");
        var chatId = GetEnvironmentValue("TELEGRAM_CHAT_ID", "");

        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
        {
            Console.WriteLine("Missing Telegram credentials. Set TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID.");
            return;
        }

        using var client = CreateHttpClient();
        var nowUtc = DateTimeOffset.UtcNow;
        var summary = await BuildDailySummaryAsync(client, nowUtc, CancellationToken.None);
        var goldMessage = await BuildGoldPriceMessageAsync(client, nowUtc, CancellationToken.None);

        if (string.IsNullOrWhiteSpace(summary) && string.IsNullOrWhiteSpace(goldMessage))
        {
            Console.WriteLine("No major items or gold price data available.");
            return;
        }

        var combined = CombineSections(summary, goldMessage);
        await SendTelegramMessageAsync(client, botToken, chatId, combined, CancellationToken.None);
        Console.WriteLine("Telegram message sent.");
    }

    private static string GetEnvironmentValue(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TodoApp-AI-Push", "1.0"));
        return client;
    }

    private static async Task<string> BuildDailySummaryAsync(
        HttpClient client,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var cutoff = nowUtc - FetchWindow;
        var tasks = Sources.Select(source => FetchFeedItemsAsync(source, client, cancellationToken)).ToArray();
        var results = await Task.WhenAll(tasks);

        var allItems = results.SelectMany(items => items)
            .Where(item => item.Published == null || item.Published >= cutoff)
            .Where(IsMajorItem)
            .OrderByDescending(item => item.Published ?? DateTimeOffset.MinValue)
            .Take(MaxItemsToShow)
            .ToList();

        if (allItems.Count == 0)
        {
            return string.Empty;
        }

        var timeZone = GetTimeZone();
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var builder = new StringBuilder();
        builder.AppendLine(EscapeMarkdownV2($"AI 大事速览 - {nowLocal:yyyy-MM-dd HH:mm}"));

        var index = 1;
        foreach (var item in allItems)
        {
            var dateText = item.Published.HasValue
                ? TimeZoneInfo.ConvertTime(item.Published.Value, timeZone).ToString("yyyy-MM-dd HH:mm")
                : "时间未知";
            var title = EscapeMarkdownV2(item.Title);
            var source = EscapeMarkdownV2(item.Source);
            var date = EscapeMarkdownV2(dateText);
            builder.AppendLine($"{index}. [{source}] {title}");
            builder.AppendLine($"   {date} | {item.Link}");
            index++;
        }

        return builder.ToString();
    }

    private static async Task<string> BuildGoldPriceMessageAsync(
        HttpClient client,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var snapshot = await FetchGoldPriceAsync(client, cancellationToken);
        if (snapshot == null)
        {
            return string.Empty;
        }

        var timeZone = GetTimeZone();
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var snapshotLocal = TimeZoneInfo.ConvertTime(snapshot.Timestamp, timeZone);
        var builder = new StringBuilder();
        builder.AppendLine(EscapeMarkdownV2($"金价快报 - {nowLocal:yyyy-MM-dd HH:mm}"));
        builder.AppendLine(EscapeMarkdownV2($"现货黄金: {snapshot.PriceUsdPerOunce:0.00} USD/oz"));
        builder.AppendLine(EscapeMarkdownV2($"时间: {snapshotLocal:yyyy-MM-dd HH:mm}"));
        builder.AppendLine(EscapeMarkdownV2("来源: metals.live"));
        return builder.ToString();
    }

    private static async Task<GoldPriceSnapshot?> FetchGoldPriceAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var url = GetEnvironmentValue("GOLD_PRICE_URL", "https://api.metals.live/v1/spot/gold");
        try
        {
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var first = doc.RootElement[0];
            if (first.ValueKind != JsonValueKind.Array || first.GetArrayLength() < 2)
            {
                return null;
            }

            var timestamp = first[0].GetInt64();
            var price = ReadDecimal(first[1]);
            if (price <= 0)
            {
                return null;
            }

            var time = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            return new GoldPriceSnapshot(price, time);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to fetch gold price: {ex.Message}");
            return null;
        }
    }

    private static decimal ReadDecimal(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDecimal(out var value) => value,
            JsonValueKind.Number => Convert.ToDecimal(element.GetDouble()),
            _ => 0m
        };
    }

    private static bool IsMajorItem(FeedItem item)
    {
        var text = $"{item.Title} {item.Summary}";
        return MajorKeywords.Any(keyword =>
            text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<FeedItem>> FetchFeedItemsAsync(
        FeedSource source,
        HttpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(source.Url, cancellationToken);
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseFeed(content, source.Name);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] Failed to fetch {source.Name}: {ex.Message}");
            return [];
        }
    }

    private static List<FeedItem> ParseFeed(string xmlContent, string sourceName)
    {
        var doc = XDocument.Parse(xmlContent);
        var items = new List<FeedItem>();

        var channel = doc.Root?.Element("channel");
        if (channel != null)
        {
            foreach (var item in channel.Elements("item"))
            {
                items.Add(ParseRssItem(item, sourceName));
            }
            return items.Where(item => !string.IsNullOrWhiteSpace(item.Title)).ToList();
        }

        XNamespace atom = "http://www.w3.org/2005/Atom";
        var feed = doc.Root;
        if (feed != null && feed.Name == atom + "feed")
        {
            foreach (var entry in feed.Elements(atom + "entry"))
            {
                items.Add(ParseAtomEntry(entry, sourceName, atom));
            }
        }

        return items.Where(item => !string.IsNullOrWhiteSpace(item.Title)).ToList();
    }

    private static FeedItem ParseRssItem(XElement item, string sourceName)
    {
        var title = item.Element("title")?.Value?.Trim() ?? "";
        var link = item.Element("link")?.Value?.Trim() ?? "";
        var pubDateText = item.Element("pubDate")?.Value?.Trim();
        DateTimeOffset? published = null;
        if (DateTimeOffset.TryParse(pubDateText, out var parsed))
        {
            published = parsed;
        }

        var description = item.Element("description")?.Value?.Trim() ?? "";
        return new FeedItem(title, link, published, description, sourceName);
    }

    private static FeedItem ParseAtomEntry(XElement entry, string sourceName, XNamespace atom)
    {
        var title = entry.Element(atom + "title")?.Value?.Trim() ?? "";
        var link = entry.Elements(atom + "link")
            .Select(el => el.Attribute("href")?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        var publishedText = entry.Element(atom + "published")?.Value?.Trim()
                            ?? entry.Element(atom + "updated")?.Value?.Trim();
        DateTimeOffset? published = null;
        if (DateTimeOffset.TryParse(publishedText, out var parsed))
        {
            published = parsed;
        }

        var summary = entry.Element(atom + "summary")?.Value?.Trim()
                      ?? entry.Element(atom + "content")?.Value?.Trim()
                      ?? "";
        return new FeedItem(title, link, published, summary, sourceName);
    }

    private static async Task SendTelegramMessageAsync(
        HttpClient client,
        string botToken,
        string chatId,
        string message,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in SplitMessage(message, TelegramMaxMessageLength))
        {
            var sent = await TrySendTelegramMessageAsync(
                client,
                botToken,
                chatId,
                chunk,
                "MarkdownV2",
                cancellationToken);

            if (!sent)
            {
                Console.WriteLine("[WARN] MarkdownV2 failed, retrying without parse_mode.");
                var fallbackSent = await TrySendTelegramMessageAsync(
                    client,
                    botToken,
                    chatId,
                    chunk,
                    null,
                    cancellationToken);

                if (!fallbackSent)
                {
                    throw new HttpRequestException("Telegram sendMessage failed after retry.");
                }
            }
        }
    }

    private static async Task<bool> TrySendTelegramMessageAsync(
        HttpClient client,
        string botToken,
        string chatId,
        string message,
        string? parseMode,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
        var payload = new List<KeyValuePair<string, string>>
        {
            new("chat_id", chatId),
            new("text", message),
            new("disable_web_page_preview", "true")
        };

        if (!string.IsNullOrWhiteSpace(parseMode))
        {
            payload.Add(new KeyValuePair<string, string>("parse_mode", parseMode));
        }

        using var content = new FormUrlEncodedContent(payload);
        using var response = await client.PostAsync(url, content, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Console.WriteLine($"[ERROR] Telegram API error {(int)response.StatusCode}: {body}");
        return false;
    }

    private static IEnumerable<string> SplitMessage(string message, int maxLength)
    {
        if (message.Length <= maxLength)
        {
            yield return message;
            yield break;
        }

        var lines = message.Split('\n');
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (builder.Length + line.Length + 1 > maxLength)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString().TrimEnd();
                    builder.Clear();
                }
            }

            builder.AppendLine(line);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString().TrimEnd();
        }
    }

    private static string EscapeMarkdownV2(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch is '_' or '*' or '[' or ']' or '(' or ')' or '~' or '`' or '>' or '#' or '+' or '-' or '='
                or '|' or '{' or '}' or '.' or '!')
            {
                builder.Append('\\');
            }
            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static TimeZoneInfo GetTimeZone()
    {
        var timeZoneId = GetEnvironmentValue("TIME_ZONE", "Asia/Shanghai");
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            Console.WriteLine($"[WARN] Time zone '{timeZoneId}' not found. Falling back to local time.");
            return TimeZoneInfo.Local;
        }
        catch (InvalidTimeZoneException)
        {
            Console.WriteLine($"[WARN] Time zone '{timeZoneId}' invalid. Falling back to local time.");
            return TimeZoneInfo.Local;
        }
    }

    private sealed record FeedSource(string Name, string Url);
    private sealed record FeedItem(string Title, string Link, DateTimeOffset? Published, string Summary, string Source);
    private sealed record GoldPriceSnapshot(decimal PriceUsdPerOunce, DateTimeOffset Timestamp);

    private static string CombineSections(params string[] sections)
    {
        var builder = new StringBuilder();
        var first = true;
        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                continue;
            }

            if (!first)
            {
                builder.AppendLine();
            }

            builder.Append(section.TrimEnd());
            first = false;
        }

        return builder.ToString();
    }
}

