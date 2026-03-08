using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotNetEnv;
using TL;
using WTelegram;
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonObject = System.Text.Json.Nodes.JsonObject;

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
    Console.Error.WriteLine("Usage: scraper <query> [--channels N] [--posts N] [--output FILE]");
    return 1;
}

string query      = args[0];
int maxChannels   = 5;
int maxPosts      = 200;
string outputFile = Path.Combine(dataDir, "results.json");

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--channels": maxChannels = int.Parse(args[++i]); break;
        case "--posts":    maxPosts    = int.Parse(args[++i]); break;
        case "--output":   outputFile  = args[++i]; break;
    }
}

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

Log($"Authenticated. Starting scrape for: '{query}'");
Log($"Searching for channels matching '{query}'...");

var searchResult = await RetryAsync(
    () => client.Contacts_Search(query, maxChannels),
    "Contacts_Search");

var channels = searchResult.results
    .OfType<PeerChannel>()
    .Select(p => searchResult.chats.GetValueOrDefault(p.channel_id) as Channel)
    .OfType<Channel>()
    .Take(maxChannels)
    .ToList();

Log($"Found {channels.Count} channel(s).");

var output = new JsonObject
{
    ["query"]      = query,
    ["scraped_at"] = DateTime.UtcNow.ToString("O"),
    ["channels"]   = new JsonArray()
};
var outputChannels = output["channels"]!.AsArray();

for (int i = 0; i < channels.Count; i++)
{
    var channel = channels[i];
    Log($"[{i + 1}/{channels.Count}] Scraping: {channel.title}");
    if (i > 0) await Task.Delay(ApiThrottle);
    try
    {
        outputChannels.Add(await RetryAsync(
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
await File.WriteAllTextAsync(outputFile, output.ToJsonString(new JsonSerializerOptions
{
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
}));

Log($"Done. Results written to {outputFile}");
return 0;

// ── helpers ───────────────────────────────────────────────────────────────────

static int ReactionCount(ReactionCount r) => r.count;

async Task<JsonObject> FetchChannelStats(Client client, Channel channel, int maxPosts)
{
    var fullInfo = await RetryAsync(
        () => client.Channels_GetFullChannel(channel),
        $"GetFullChannel:{channel.title}");
    var fullChat = (ChannelFull)fullInfo.full_chat;
    long subscribers = fullChat.participants_count;

    await Task.Delay(ApiThrottle);

    Log($"  Fetching posts (last 30 days) from @{channel.username ?? channel.id.ToString()}...");

    var cutoff    = DateTime.UtcNow.AddDays(-30);
    var oneDayAgo = DateTime.UtcNow.AddDays(-1);

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

    double avgReachPct = 0, avgReachFirstDayPct = 0, avgForwards = 0, avgComments = 0, avgReactions = 0;

    if (posts.Count > 0)
    {
        avgReachPct = subscribers > 0
            ? posts.Average(p => (double)p.views) / subscribers * 100
            : 0;

        // First-day reach proxy: posts that are 24-72h old have views ≈ day-1 views
        // (most Telegram views accumulate within the first day).
        // Fall back to all posts older than 24h if not enough data points.
        var firstDayPosts = posts.Where(p => p.date.ToUniversalTime() < oneDayAgo
                                          && p.date.ToUniversalTime() >= DateTime.UtcNow.AddDays(-3)).ToList();
        if (firstDayPosts.Count < 3)
            firstDayPosts = [.. posts.Where(p => p.date.ToUniversalTime() < oneDayAgo)];

        avgReachFirstDayPct = firstDayPosts.Count > 0 && subscribers > 0
            ? firstDayPosts.Average(p => (double)p.views) / subscribers * 100
            : 0;

        avgForwards  = posts.Average(p => (double)p.forwards);
        avgComments  = posts.Average(p => (double)(p.replies?.replies ?? 0));
        avgReactions = posts.Sum(p => p.reactions?.results?.Sum(ReactionCount) ?? 0) / (double)posts.Count;
    }

    return new JsonObject
    {
        ["id"]                      = channel.id,
        ["username"]                = channel.username,
        ["title"]                   = channel.title,
        ["subscribers"]             = subscribers,
        ["posts_analyzed"]          = posts.Count,
        ["avg_reach_pct"]           = Math.Round(avgReachPct, 1),
        ["avg_reach_first_day_pct"] = Math.Round(avgReachFirstDayPct, 1),
        ["avg_forwards"]            = Math.Round(avgForwards, 1),
        ["avg_comments"]            = Math.Round(avgComments, 1),
        ["avg_reactions"]           = Math.Round(avgReactions, 1),
    };
}
