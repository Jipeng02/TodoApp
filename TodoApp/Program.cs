using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

public class Program
{
    private static readonly TimeSpan DefaultPushTimeLocal = new(9, 0, 0);
    private static readonly TimeSpan FetchWindow = TimeSpan.FromHours(24);
    private static readonly int MaxItemsToShow = 12;

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
        var timeZoneId = GetEnvironmentValue("TIME_ZONE", "Asia/Shanghai");
        var pushTime = ParsePushTime(GetEnvironmentValue("PUSH_TIME", "09:00"));

        var timeZone = ResolveTimeZone(timeZoneId);
        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);

        if (!ShouldRunNow(nowLocal, pushTime))
        {
            Console.WriteLine($"Skip run: now {nowLocal:yyyy-MM-dd HH:mm} in {timeZone.Id}, target {pushTime:hh\\:mm}.");
            return;
        }

        var botToken = GetEnvironmentValue("TELEGRAM_BOT_TOKEN", "");
        var chatId = GetEnvironmentValue("TELEGRAM_CHAT_ID", "");

        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
        {
            Console.WriteLine("Missing Telegram credentials. Set TELEGRAM_BOT_TOKEN and TELEGRAM_CHAT_ID.");
            return;
        }

        using var client = CreateHttpClient();
        var summary = await BuildDailySummaryAsync(client, nowUtc, CancellationToken.None);

        if (string.IsNullOrWhiteSpace(summary))
        {
            Console.WriteLine("No major items found in the last 24 hours.");
            return;
        }

        await SendTelegramMessageAsync(client, botToken, chatId, summary, CancellationToken.None);
        Console.WriteLine("Telegram message sent.");
    }

    private static string GetEnvironmentValue(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static TimeSpan ParsePushTime(string value)
    {
        if (TimeSpan.TryParseExact(value, "hh\\:mm", CultureInfo.InvariantCulture, out var time))
        {
            return time;
        }

        return DefaultPushTimeLocal;
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            Console.WriteLine($"Unknown TIME_ZONE '{timeZoneId}', fallback to UTC.");
            return TimeZoneInfo.Utc;
        }
    }

    private static bool ShouldRunNow(DateTimeOffset nowLocal, TimeSpan targetTime)
    {
        return nowLocal.Hour == targetTime.Hours && nowLocal.Minute == targetTime.Minutes;
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

        var builder = new StringBuilder();
        builder.AppendLine(EscapeMarkdownV2($"AI 大事速览 - {DateTimeOffset.Now:yyyy-MM-dd}"));

        var index = 1;
        foreach (var item in allItems)
        {
            var dateText = item.Published?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "时间未知";
            var title = EscapeMarkdownV2(item.Title);
            var source = EscapeMarkdownV2(item.Source);
            var date = EscapeMarkdownV2(dateText);
            var link = EscapeMarkdownV2(item.Link);
            builder.AppendLine($"{index}. [{source}] {title}");
            builder.AppendLine($"   {date} | {link}");
            index++;
        }

        return builder.ToString();
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
        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";
        using var content = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("chat_id", chatId),
            new KeyValuePair<string, string>("text", message),
            new KeyValuePair<string, string>("parse_mode", "MarkdownV2"),
            new KeyValuePair<string, string>("disable_web_page_preview", "true")
        ]);

        using var response = await client.PostAsync(url, content, cancellationToken);
        response.EnsureSuccessStatusCode();
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

    private sealed record FeedSource(string Name, string Url);
    private sealed record FeedItem(string Title, string Link, DateTimeOffset? Published, string Summary, string Source);
}
