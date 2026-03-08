# tg-scraper

CLI tool that scrapes Telegram channels from a list and outputs per-channel engagement statistics to CSV.

Uses [WTelegramClient](https://github.com/wiz0u/WTelegramClient) (MTProto API) — real API access, not web scraping.

## Prerequisites

- A Telegram account
- API credentials from [my.telegram.org/apps](https://my.telegram.org/apps)
- Docker (recommended) **or** .NET 9 SDK
- **ARM64 architecture** (Apple Silicon / linux/arm64) — the Docker image is built for `linux-arm64`; x86_64 hosts are not supported

## Configuration

```bash
cp .env.example .env
```

Edit `.env`:

```
API_ID=12345678
API_HASH=abc123def456...
PHONE=+79001234567
```

---

## Channel list file

Create a text file with one channel per line. Supported formats:

```
# comments are ignored
https://t.me/durov
t.me/telegram
@channel_username
plain_username
```

---

## Running with Docker (recommended)

**First run** — requires interactive TTY to enter the SMS code:

```bash
docker compose run --rm -it scraper --file /data/channels.txt --posts 50
```

The scraper will prompt:
```
Telegram verification code: _
```

After entering the code, the session is saved to `./data/tg_scraper.session`.

**Subsequent runs** — no interaction needed:

```bash
docker compose run --rm scraper --file /data/channels.txt
```

Results are written to `./data/results.csv`.

**Rebuild image** after code changes:

```bash
docker compose build
```

---

## Running without Docker

```bash
dotnet run -- --file channels.txt [OPTIONS]
```

Session file is saved as `tg_scraper.session` in the current directory.

---

## Options

| Flag | Default | Description |
|------|---------|-------------|
| `--file FILE` | *(required)* | Path to file with channel links (one per line) |
| `--posts N` | 200 | Max posts to analyse per channel (within 30-day window) |
| `--output FILE` | `data/results.csv` | Output file path |

**Examples:**

```bash
docker compose run --rm scraper --file /data/channels.txt
docker compose run --rm scraper --file /data/channels.txt --posts 500
docker compose run --rm scraper --file /data/channels.txt --output /data/report.csv
```

---

## Output Format

UTF-8 CSV with a header row, one row per channel:

```
id,username,title,subscribers,posts_analyzed,avg_reach_pct,avg_reach_first_day_pct,avg_forwards,avg_comments,avg_reactions
1234567890,channel_username,Channel Title,50000,87,142.3,118.7,12.4,5.1,38.9
```

### Fields

| Field | Description |
|-------|-------------|
| `subscribers` | Total subscriber count |
| `posts_analyzed` | Number of posts collected within the last 30 days |
| `avg_reach_pct` | Avg views / subscribers × 100. Can exceed 100% when non-subscribers read the channel via shares or search |
| `avg_reach_first_day_pct` | Same ratio, but computed only on posts that are 24–72 hours old — a proxy for first-day engagement, since most Telegram views accumulate within the first day |
| `avg_forwards` | Average number of forwards per post |
| `avg_comments` | Average number of comments per post |
| `avg_reactions` | Average total reactions per post |

---

## Security

Never commit `.env` or `*.session` — both contain sensitive credentials. Both are listed in `.gitignore`.

## Rate Limits

Telegram enforces flood limits. The scraper handles them automatically with exponential backoff and server-supplied wait times.
