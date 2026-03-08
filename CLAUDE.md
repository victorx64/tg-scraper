# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single-file CLI tool (`Program.cs`) that searches Telegram channels by query string and outputs per-channel engagement statistics to JSON. Uses [WTelegramClient](https://github.com/wiz0u/WTelegramClient) (MTProto API) — real Telegram API access, not web scraping. Written in C# targeting .NET 9.

## Running

**First run** (interactive TTY required for SMS code):
```bash
docker compose run --rm -it scraper "machine learning" --channels 2 --posts 5
```

**Subsequent runs** (session cached in `./data/tg_scraper.session`):
```bash
docker compose run --rm scraper "crypto news" --channels 5
```

**Without Docker:**
```bash
dotnet run -- "QUERY" [OPTIONS]
```

**Build only (no run):**
```bash
dotnet build
```

**Rebuild image after code changes:**
```bash
docker compose build
```

## Configuration

Copy `.env.example` to `.env` and set `API_ID`, `API_HASH`, `PHONE`. Credentials come from [my.telegram.org/apps](https://my.telegram.org/apps).

## CLI options

| Flag | Default | Description |
|------|---------|-------------|
| `--channels N` | 5 | Max channels to scrape |
| `--posts N` | 200 | Max posts to analyse per channel (within 30-day window) |
| `--output FILE` | `data/results.json` | Output path |

## Architecture

Everything lives in `Program.cs` (top-level statements, single async flow):

- Channel search — `client.Contacts_Search()` finds channels matching the query
- `FetchChannelStats()` — calls `Channels_GetFullChannel` for metadata (subscriber count), then paginates `Messages_GetHistory` collecting posts from the last 30 days (up to `--posts`); computes per-channel stats
- `RetryAsync<T>()` — wraps all API calls; FloodWait (420) sleeps server-supplied seconds, other errors use exponential backoff (2s, 4s, 8s…), default 3 attempts
- `ApiThrottle` — 500ms fixed delay injected between every API call to stay under rate limits

### Output fields per channel

| Field | Description |
|-------|-------------|
| `subscribers` | Total subscriber count |
| `posts_analyzed` | Number of posts collected (last 30 days) |
| `avg_reach_pct` | Avg views / subscribers × 100 (can exceed 100% via sharing) |
| `avg_reach_first_day_pct` | Same ratio but only for posts 24–72 h old (proxy for first-day views; falls back to all posts > 24 h if fewer than 3 in that window) |
| `avg_forwards` | Avg forwards per post |
| `avg_comments` | Avg comment count per post |
| `avg_reactions` | Avg total reactions per post |

Output is written as a single indented JSON to `--output` (default `data/results.json`). The `data/` directory is Docker-volume-mounted so session and output files persist on the host.

`DATA_DIR` env var controls where the session file and default output are written (set to `/data` in Docker, `.` locally).

## Project files

- `Program.cs` — all scraper logic
- `scraper.csproj` — .NET 9 project, depends on `WTelegramClient` and `DotNetEnv`
- `Dockerfile` — multi-stage build: SDK image compiles a self-contained single-file binary, runtime-deps image runs it
- `docker-compose.yml` — mounts `./data` as `/data`, passes `.env`
- `tg-scraper.slnx` — solution file
