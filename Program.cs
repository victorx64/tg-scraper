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
    Console.Error.WriteLine("Usage: scraper <query> [--channels N] [--posts N] [--comments N] [--output FILE]");
    return 1;
}

string query       = args[0];
int maxChannels    = 5;
int maxPosts       = 50;
int maxComments    = 100;
string outputFile  = Path.Combine(dataDir, "results.json");

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--channels": maxChannels = int.Parse(args[++i]); break;
        case "--posts":    maxPosts    = int.Parse(args[++i]); break;
        case "--comments": maxComments = int.Parse(args[++i]); break;
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
            () => FetchChannelData(client, channel, maxPosts, maxComments),
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

async Task<JsonObject> FetchChannelData(Client client, Channel channel, int maxPosts, int maxComments)
{
    var fullInfo = await RetryAsync(
        () => client.Channels_GetFullChannel(channel),
        $"GetFullChannel:{channel.title}");
    var fullChat = (ChannelFull)fullInfo.full_chat;

    await Task.Delay(ApiThrottle);

    var channelNode = new JsonObject
    {
        ["id"]            = channel.id,
        ["username"]      = channel.username,
        ["title"]         = channel.title,
        ["description"]   = fullChat.about ?? "",
        ["members_count"] = fullChat.participants_count,
        ["posts"]         = new JsonArray()
    };
    var posts = channelNode["posts"]!.AsArray();

    Log($"  Fetching up to {maxPosts} posts from @{channel.username ?? channel.id.ToString()}...");

    var history = await RetryAsync(
        () => client.Messages_GetHistory(channel.ToInputPeer(), limit: maxPosts),
        $"GetHistory:{channel.title}");

    foreach (var msgBase in history.Messages.OfType<Message>())
    {
        if (string.IsNullOrEmpty(msgBase.message)) continue;

        Log($"    Post {msgBase.id}: fetching comments...");
        await Task.Delay(ApiThrottle);
        posts.Add(new JsonObject
        {
            ["id"]             = msgBase.id,
            ["date"]           = msgBase.date.ToUniversalTime().ToString("O"),
            ["text"]           = msgBase.message,
            ["views"]          = msgBase.views,
            ["forwards"]       = msgBase.forwards,
            ["comments_count"] = msgBase.replies?.replies ?? 0,
            ["comments"]       = await FetchComments(client, channel.ToInputPeer(), msgBase.id, maxComments)
        });
    }

    return channelNode;
}

async Task<JsonArray> FetchComments(Client client, InputPeer peer, int msgId, int limit)
{
    var arr = new JsonArray();
    // These errors mean the post has no comment section — expected, not worth retrying.
    static bool IsTransient(Exception ex) =>
        ex is not RpcException rpc || rpc.Message is not ("MSG_ID_INVALID" or "CHAT_ID_INVALID" or "PEER_ID_INVALID");

    try
    {
        var result = await RetryAsync(
            () => client.Messages_GetReplies(peer, msgId, limit: limit),
            $"GetReplies:{msgId}",
            shouldRetry: IsTransient);

        foreach (var msgBase in result.Messages.OfType<Message>())
        {
            string? author = null;
            if (msgBase.from_id != null)
                author = (result.UserOrChat(msgBase.from_id) as User)?.username;

            arr.Add(new JsonObject
            {
                ["id"]     = msgBase.id,
                ["date"]   = msgBase.date.ToUniversalTime().ToString("O"),
                ["text"]   = msgBase.message ?? "",
                ["author"] = author
            });
        }
    }
    catch (RpcException ex) when (ex.Message is "MSG_ID_INVALID" or "CHAT_ID_INVALID") { }
    catch (Exception ex)
    {
        Log($"    [GetReplies:{msgId}] {ex.GetType().Name}: {ex.Message}");
    }
    return arr;
}
