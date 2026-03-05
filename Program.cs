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

static void Log(string msg) => Console.Error.WriteLine(msg);

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

var searchResult = await client.Contacts_Search(query, maxChannels);
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
    try
    {
        outputChannels.Add(await FetchChannelData(client, channel, maxPosts, maxComments));
    }
    catch (RpcException ex) when (ex.Code == 420)
    {
        var secs = ParseFloodWait(ex.Message);
        Log($"  FloodWait: sleeping {secs}s then retrying...");
        await Task.Delay(secs * 1000);
        try
        {
            outputChannels.Add(await FetchChannelData(client, channel, maxPosts, maxComments));
        }
        catch (Exception retryEx)
        {
            Log($"  Failed after retry: {retryEx.Message}");
        }
    }
    catch (Exception ex)
    {
        Log($"  Skipping due to error: {ex.Message}");
    }
}

var outDir = Path.GetDirectoryName(Path.GetFullPath(outputFile));
if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
await File.WriteAllTextAsync(outputFile, output.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

Log($"Done. Results written to {outputFile}");
return 0;

// ── helpers ───────────────────────────────────────────────────────────────────

async Task<JsonObject> FetchChannelData(Client client, Channel channel, int maxPosts, int maxComments)
{
    var fullInfo = await client.Channels_GetFullChannel(channel);
    var fullChat = (ChannelFull)fullInfo.full_chat;

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

    var history = await client.Messages_GetHistory(channel.ToInputPeer(), limit: maxPosts);
    foreach (var msgBase in history.Messages.OfType<Message>())
    {
        if (string.IsNullOrEmpty(msgBase.message)) continue;

        Log($"    Post {msgBase.id}: fetching comments...");
        posts.Add(new JsonObject
        {
            ["id"]            = msgBase.id,
            ["date"]          = msgBase.date.ToUniversalTime().ToString("O"),
            ["text"]          = msgBase.message,
            ["views"]         = msgBase.views,
            ["forwards"]      = msgBase.forwards,
            ["comments_count"] = msgBase.replies?.replies ?? 0,
            ["comments"]      = await FetchComments(client, channel.ToInputPeer(), msgBase.id, maxComments)
        });
    }

    return channelNode;
}

async Task<JsonArray> FetchComments(Client client, InputPeer peer, int msgId, int limit)
{
    var arr = new JsonArray();
    try
    {
        Messages_MessagesBase result = await client.Messages_GetReplies(peer, msgId, limit: limit);
        foreach (var msgBase in result.Messages.OfType<Message>())
        {
            string? author = null;
            if (msgBase.from_id != null)
            {
                author = (result.UserOrChat(msgBase.from_id) as User)?.username;
            }
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
    catch (RpcException ex) when (ex.Code == 420)
    {
        var secs = ParseFloodWait(ex.Message);
        Log($"    FloodWait on comments: sleeping {secs}s");
        await Task.Delay(secs * 1000);
    }
    return arr;
}
