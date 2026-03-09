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
    Console.Error.WriteLine("Usage: scraper --file CHANNELS_FILE [--posts N] [--output FILE] [--concurrency N]");
    Console.Error.WriteLine("  CHANNELS_FILE: one channel link per line (t.me/x, @x, or x)");
    return 1;
}

string? channelsFile = null;
int maxPosts         = 200;
int concurrency      = 2;
string outputFile    = Path.Combine(dataDir, "results.csv");

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--file":        channelsFile = args[++i]; break;
        case "--posts":       maxPosts     = int.Parse(args[++i]); break;
        case "--output":      outputFile   = args[++i]; break;
        case "--concurrency": concurrency  = int.Parse(args[++i]); break;
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
static async Task<T> RetryAsync<T>(Func<Task<T>> action, string ctx, int maxAttempts = 3,
    Func<Exception, bool>? shouldRetry = null)
{
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            return await action();
        }
        catch (RpcException ex) when (ex.Message.Contains("USERNAME_NOT_OCCUPIED"))
        {
            throw; // non-retryable
        }
        catch (RpcException ex) when (ex.Message.Contains("USERNAME_INVALID"))
        {
            throw; // non-retryable
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

Log($"Authenticated. {channelUsernames.Count} channel(s) from {channelsFile}.");

// ── Resume: load already-processed usernames from existing CSV ────────────────
var outDir = Path.GetDirectoryName(Path.GetFullPath(outputFile));
if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

var alreadyDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
bool csvExists = File.Exists(outputFile);
if (csvExists)
{
    var existingLines = await File.ReadAllLinesAsync(outputFile);
    foreach (var line in existingLines.Skip(1)) // skip header
    {
        var cols = line.Split(',');
        if (cols.Length >= 2 && !string.IsNullOrWhiteSpace(cols[1]))
            alreadyDone.Add(cols[1].Trim('"'));
    }
    Log($"Resume: {alreadyDone.Count} channel(s) already in output, skipping them.");
}

var toProcess = channelUsernames.Where(u => !alreadyDone.Contains(u)).ToList();
Log($"To process: {toProcess.Count} channel(s). Concurrency: {concurrency}.");

// ── CSV writer (append-safe, thread-safe) ─────────────────────────────────────
var csvLock = new SemaphoreSlim(1, 1);
var csvWriter = new StreamWriter(outputFile, append: csvExists, encoding: Encoding.UTF8) { AutoFlush = true };
if (!csvExists)
    await csvWriter.WriteLineAsync("id,username,title,subscribers,posts_analyzed,avg_reach_pct,avg_engagement_rate_pct");

int processed = 0;
int failed    = 0;

// ── Resolve + fetch stats in parallel ────────────────────────────────────────
int total = toProcess.Count;

await Parallel.ForEachAsync(toProcess,
    new ParallelOptions { MaxDegreeOfParallelism = concurrency },
    async (username, _) =>
    {
        try
        {
            await Task.Delay(ApiThrottle, _);
            var resolved = await RetryAsync(
                () => client.Contacts_ResolveUsername(username),
                $"Resolve:{username}");
            if (resolved.Chat is not Channel channel)
            {
                Log($"  @{username} is not a channel, skipping.");
                return;
            }

            var stats = await RetryAsync(
                () => FetchChannelStats(client, channel, maxPosts, ApiThrottle),
                $"channel:{channel.title}");

            await csvLock.WaitAsync(_);
            try
            {
                await csvWriter.WriteLineAsync(
                    $"{stats.Id},{CsvCell(stats.Username ?? "")},{CsvCell(stats.Title)},{stats.Subscribers},{stats.PostsAnalyzed},{stats.AvgReachPct},{stats.AvgEngagementRatePct}");
            }
            finally { csvLock.Release(); }

            int n = Interlocked.Increment(ref processed);
            Log($"[{n}/{total}] Done: {channel.title}");
        }
        catch (Exception ex)
        {
            int n = Interlocked.Increment(ref failed);
            Log($"  [@{username}] Failed: {ex.GetType().Name}: {ex.Message}  (total failed: {n})");
        }
    });

await csvWriter.DisposeAsync();
Log($"Done. {processed} succeeded, {failed} failed. Results: {outputFile}");

var xlsxFile = Path.ChangeExtension(outputFile, ".xlsx");
ConvertCsvToXlsx(outputFile, xlsxFile);
Log($"Excel: {xlsxFile}");
return 0;

// ── helpers ───────────────────────────────────────────────────────────────────

static void ConvertCsvToXlsx(string csvPath, string xlsxPath)
{
    using var wb = new ClosedXML.Excel.XLWorkbook();
    var ws = wb.AddWorksheet("Results");
    var lines = File.ReadAllLines(csvPath);
    for (int row = 0; row < lines.Length; row++)
    {
        var cols = ParseCsvLine(lines[row]);
        for (int col = 0; col < cols.Count; col++)
            ws.Cell(row + 1, col + 1).Value = cols[col];
    }
    wb.SaveAs(xlsxPath);
}

static List<string> ParseCsvLine(string line)
{
    var fields = new List<string>();
    var sb = new StringBuilder();
    bool inQuotes = false;
    for (int i = 0; i < line.Length; i++)
    {
        char c = line[i];
        if (inQuotes)
        {
            if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
            else if (c == '"') inQuotes = false;
            else sb.Append(c);
        }
        else
        {
            if (c == '"') inQuotes = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
    }
    fields.Add(sb.ToString());
    return fields;
}

static string? ParseUsername(string line)
{
    var s = line;
    if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = s["https://".Length..];
    if (s.StartsWith("http://",  StringComparison.OrdinalIgnoreCase)) s = s["http://".Length..];
    if (s.StartsWith("t.me/",    StringComparison.OrdinalIgnoreCase)) s = s["t.me/".Length..];
    s = s.TrimStart('@').Trim();
    return string.IsNullOrEmpty(s) ? null : s;
}

static string CsvCell(string value) =>
    value.Contains(',') || value.Contains('"') || value.Contains('\n')
        ? $"\"{value.Replace("\"", "\"\"")}\""
        : value;

static int ReactionCount(ReactionCount r) => r.count;

static async Task<ChannelStats> FetchChannelStats(Client client, Channel channel, int maxPosts, TimeSpan throttle)
{
    var fullInfo = await RetryAsync(
        () => client.Channels_GetFullChannel(channel),
        $"GetFullChannel:{channel.title}");
    var fullChat = (ChannelFull)fullInfo.full_chat;
    long subscribers = fullChat.participants_count;

    await Task.Delay(throttle);

    var cutoff = DateTime.UtcNow.AddDays(-30);
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
        await Task.Delay(throttle);
    }

    double avgReachPct = 0, avgEngagementRatePct = 0;

    if (posts.Count > 0)
    {
        avgReachPct  = subscribers > 0
            ? posts.Average(p => (double)p.views) / subscribers * 100
            : 0;
        avgEngagementRatePct = posts.Average(p =>
        {
            double views = p.views;
            if (views <= 0) return 0;
            double activities = p.forwards
                + (p.replies?.replies ?? 0)
                + (p.reactions?.results?.Sum(ReactionCount) ?? 0);
            return activities / views * 100;
        });
    }

    return new ChannelStats(
        channel.id,
        channel.username,
        channel.title,
        subscribers,
        posts.Count,
        Math.Round(avgReachPct, 1),
        Math.Round(avgEngagementRatePct, 2));
}

record ChannelStats(
    long Id, string? Username, string Title, long Subscribers,
    int PostsAnalyzed, double AvgReachPct,
    double AvgEngagementRatePct);
