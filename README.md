# tg-scraper

CLI tool that searches Telegram channels by query string and scrapes posts + comments to JSON.

Uses [WTelegramClient](https://github.com/wiz0u/WTelegramClient) (MTProto API) — real API access, not web scraping.

## Prerequisites

- A Telegram account
- API credentials from [my.telegram.org/apps](https://my.telegram.org/apps)
- Docker (recommended) **or** .NET 9 SDK

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

## Running with Docker (recommended)

**First run** — requires interactive TTY to enter the SMS code:

```bash
docker compose run --rm -it scraper "machine learning" --channels 2 --posts 5
```

The scraper will prompt:
```
Telegram verification code: _
```

After entering the code, the session is saved to `./data/tg_scraper.session`.

**Subsequent runs** — no interaction needed:

```bash
docker compose run --rm scraper "crypto news" --channels 5
```

Results are written to `./data/results.json`.

**Rebuild image** after code changes:

```bash
docker compose build
```

---

## Running without Docker

```bash
dotnet run -- "QUERY" [OPTIONS]
```

Session file is saved as `tg_scraper.session` in the current directory.

---

## Options

| Flag | Default | Description |
|------|---------|-------------|
| `--channels N` | 5 | Max number of channels to scrape |
| `--posts N` | 50 | Max posts per channel |
| `--comments N` | 100 | Max comments per post |
| `--output FILE` | `data/results.json` | Output file path |

**Examples:**

```bash
docker compose run --rm scraper "machine learning"
docker compose run --rm scraper "politics" --channels 10 --posts 100 --comments 200
docker compose run --rm scraper "tech" --output /data/tech.json
```

---

## Output Format

```json
{
  "query": "machine learning",
  "scraped_at": "2026-03-05T12:00:00+00:00",
  "channels": [
    {
      "id": 1234567890,
      "username": "channel_username",
      "title": "Channel Title",
      "description": "Channel description text",
      "members_count": 50000,
      "posts": [
        {
          "id": 456,
          "date": "2026-03-04T08:30:00+00:00",
          "text": "Post content here",
          "views": 12000,
          "forwards": 340,
          "comments_count": 87,
          "comments": [
            {
              "id": 789,
              "date": "2026-03-04T09:00:00+00:00",
              "text": "Comment text",
              "author": "username_or_null"
            }
          ]
        }
      ]
    }
  ]
}
```

- Posts with no text (media-only) are skipped
- Channels without a linked discussion group will have `"comments": []`

---

## Security

Never commit `.env` or `*.session` — both contain sensitive credentials. Both are listed in `.gitignore`.

## Rate Limits

Telegram enforces flood limits. The scraper handles them automatically:
- Per channel: sleeps the required time and retries once
- Per comment batch: sleeps and skips (returns empty comments for that post)
