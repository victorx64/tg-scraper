using System.Text;
using DotNetEnv;
using TL;
using WTelegram;

// Suppress WTelegramClient internal noise
Helpers.Log = (_, _) => { };

// Load .env if present (TraversePath walks up from cwd)
Env.TraversePath().Load();

var apiId   = Environment.GetEnvironmentVariable("API_ID")   ?? throw new Exception("API_ID not set");
var apiHash = Environment.GetEnvironmentVariable("API_HASH") ?? throw new Exception("API_HASH not set");
var phone   = Environment.GetEnvironmentVariable("PHONE")    ?? throw new Exception("PHONE not set");
var dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? ".";

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("Usage: scraper --file CHANNELS_FILE [--posts N] [--output FILE]");
    Console.Error.WriteLine("  CHANNELS_FILE: one channel link per line (t.me/x, @x, or x)");
    return 1;
}

string? channelsFile = null;
int maxPosts         = 200;
string outputFile    = Path.Combine(dataDir, "results.csv");

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--file":   channelsFile = args[++i]; break;
        case "--posts":  maxPosts     = int.Parse(args[++i]); break;
        case "--output": outputFile   = args[++i]; break;
    }
}

if (channelsFile == null)
{
    Console.Error.WriteLine("Error: --file is required.");
    return 1;
}

if (!File.Exists(channelsFile))
{
    Console.Error.WriteLine($"Error: file not found: {channelsFile}");
    return 1;
}

var channelUsernames = File.ReadAllLines(channelsFile)
    .Select(line => line.Trim())
    .Where(line => !string.IsNullOrEmpty(line) && !line.StartsWith('#'))
    .Select(ParseUsername)
    .OfType<string>()
    .ToList();

static void Log(string msg) =>
    Console.Error.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {msg}");

static string Prompt(string text)
{
    Console.Error.Write(text);
    return Console.ReadLine() ?? "";
}

static int ParseFloodWait(string message)
{
    var parts = message.Split('_');
    return int.TryParse(parts[^1], out var s) ? s : 30;
}

// Delay between API requests to avoid rate-limit bans
TimeSpan ApiThrottle = TimeSpan.FromMilliseconds(500);

// Retry with exponential backoff; FloodWait uses server-supplied delay.
// shouldRetry: return false to let the exception propagate immediately without retrying.
static async Task<T> RetryAsync<T>(Func<Task<T>> action, string ctx, int maxAttempts = 3,
    Func<Exception, bool>? shouldRetry = null)
{
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            return await action();
        }
        catch (RpcException ex) when (ex.Code == 420)
        {
            int secs = ParseFloodWait(ex.Message);
            Log($"[{ctx}] FloodWait: sleeping {secs}s (attempt {attempt}/{maxAttempts})");
            if (attempt >= maxAttempts) throw;
            await Task.Delay(secs * 1000);
        }
        catch (Exception ex) when (attempt < maxAttempts && (shouldRetry == null || shouldRetry(ex)))
        {
            int delaySecs = (int)Math.Pow(2, attempt); // 2s, 4s, 8s...
            Log($"[{ctx}] {ex.GetType().Name}: {ex.Message} — retry in {delaySecs}s (attempt {attempt}/{maxAttempts})");
            await Task.Delay(delaySecs * 1000);
        }
    }
}

string sessionPath = Path.Combine(dataDir, "tg_scraper.session");

string? Config(string what) => what switch
{
    "api_id"            => apiId,
    "api_hash"          => apiHash,
    "phone_number"      => phone,
    "verification_code" => Prompt("Telegram verification code: "),
    "session_pathname"  => sessionPath,
    _                   => null
};

using var client = new Client(Config);
await client.LoginUserIfNeeded();

Log($"Authenticated. Resolving {channelUsernames.Count} channel(s) from {channelsFile}...");

var channels = new List<Channel>();
foreach (var username in channelUsernames)
{
    try
    {
        await Task.Delay(ApiThrottle);
        var resolved = await RetryAsync(
            () => client.Contacts_ResolveUsername(username),
            $"ResolveUsername:{username}");
        if (resolved.Chat is Channel ch)
        {
            channels.Add(ch);
            Log($"  Resolved @{username} → {ch.title}");
        }
        else
        {
            Log($"  @{username} is not a channel, skipping.");
        }
    }
    catch (Exception ex)
    {
        Log($"  Failed to resolve @{username}: {ex.Message}");
    }
}

Log($"Resolved {channels.Count} channel(s).");

var rows = new List<ChannelStats>();

for (int i = 0; i < channels.Count; i++)
{
    var channel = channels[i];
    Log($"[{i + 1}/{channels.Count}] Scraping: {channel.title}");
    if (i > 0) await Task.Delay(ApiThrottle);
    try
    {
        rows.Add(await RetryAsync(
            () => FetchChannelStats(client, channel, maxPosts),
            $"channel:{channel.title}"));
    }
    catch (Exception ex)
    {
        Log($"  [{channel.title}] Failed after retries: {ex.GetType().Name}: {ex.Message}");
    }
}

var outDir = Path.GetDirectoryName(Path.GetFullPath(outputFile));
if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

var csv = new StringBuilder();
csv.AppendLine("id,username,title,subscribers,posts_analyzed,avg_reach_pct,avg_forwards,avg_comments,avg_reactions");
foreach (var r in rows)
    csv.AppendLine($"{r.Id},{CsvCell(r.Username ?? "")},{CsvCell(r.Title)},{r.Subscribers},{r.PostsAnalyzed},{r.AvgReachPct},{r.AvgForwards},{r.AvgComments},{r.AvgReactions}");

await File.WriteAllTextAsync(outputFile, csv.ToString(), Encoding.UTF8);

Log($"Done. Results written to {outputFile}");
return 0;

// ── helpers ───────────────────────────────────────────────────────────────────

// Extracts username from t.me/x, https://t.me/x, @x, or plain x
static string? ParseUsername(string line)
{
    // Strip URL prefix
    var s = line;
    if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s["https://".Length..];
    if (s.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)) s = s["http://".Length..];
    if (s.StartsWith("t.me/",    StringComparison.OrdinalIgnoreCase)) s = s["t.me/".Length..];
    // Strip leading @
    s = s.TrimStart('@').Trim();
    return string.IsNullOrEmpty(s) ? null : s;
}

static string CsvCell(string value) =>
    value.Contains(',') || value.Contains('"') || value.Contains('\n')
        ? $"\"{value.Replace("\"", "\"\"")}\""
        : value;

static int ReactionCount(ReactionCount r) => r.count;

async Task<ChannelStats> FetchChannelStats(Client client, Channel channel, int maxPosts)
{
    var fullInfo = await RetryAsync(
        () => client.Channels_GetFullChannel(channel),
        $"GetFullChannel:{channel.title}");
    var fullChat = (ChannelFull)fullInfo.full_chat;
    long subscribers = fullChat.participants_count;

    await Task.Delay(ApiThrottle);

    Log($"  Fetching posts (last 30 days) from @{channel.username ?? channel.id.ToString()}...");

    var cutoff = DateTime.UtcNow.AddDays(-30);

    // Collect posts within 30-day window, paginating as needed
    var posts = new List<Message>();
    int offsetId = 0;

    while (posts.Count < maxPosts)
    {
        var batch = await RetryAsync(
            () => client.Messages_GetHistory(channel.ToInputPeer(), offset_id: offsetId, limit: 100),
            $"GetHistory:{channel.title}");

        var msgs = batch.Messages.OfType<Message>().ToList();
        if (msgs.Count == 0) break;

        bool reachedCutoff = false;
        foreach (var msg in msgs)
        {
            if (msg.date.ToUniversalTime() < cutoff) { reachedCutoff = true; break; }
            posts.Add(msg);
        }

        if (reachedCutoff || posts.Count >= maxPosts) break;
        offsetId = msgs[^1].id;
        await Task.Delay(ApiThrottle);
    }

    Log($"  Loaded {posts.Count} post(s) for @{channel.username ?? channel.id.ToString()}.");

    double avgReachPct = 0, avgForwards = 0, avgComments = 0, avgReactions = 0;

    if (posts.Count > 0)
    {
        avgReachPct = subscribers > 0
            ? posts.Average(p => (double)p.views) / subscribers * 100
            : 0;

        avgForwards  = posts.Average(p => (double)p.forwards);
        avgComments  = posts.Average(p => (double)(p.replies?.replies ?? 0));
        avgReactions = posts.Sum(p => p.reactions?.results?.Sum(ReactionCount) ?? 0) / (double)posts.Count;
    }

    return new ChannelStats(
        channel.id,
        channel.username,
        channel.title,
        subscribers,
        posts.Count,
        Math.Round(avgReachPct, 1),
        Math.Round(avgForwards, 1),
        Math.Round(avgComments, 1),
        Math.Round(avgReactions, 1));
}

record ChannelStats(
    long Id, string? Username, string Title, long Subscribers,
    int PostsAnalyzed, double AvgReachPct,
    double AvgForwards, double AvgComments, double AvgReactions);
