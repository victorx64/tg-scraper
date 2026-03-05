# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A single-file CLI tool (`Program.cs`) that searches Telegram channels by query string and scrapes posts + comments to JSON. Uses [WTelegramClient](https://github.com/wiz0u/WTelegramClient) (MTProto API) — real Telegram API access, not web scraping. Written in C# targeting .NET 9.

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
| `--posts N` | 50 | Max posts per channel |
| `--comments N` | 100 | Max comments per post |
| `--output FILE` | `data/results.json` | Output path |

## Architecture

Everything lives in `Program.cs` (top-level statements, single async flow):

- Channel search — `client.Contacts_Search()` finds channels matching the query
- `FetchChannelData()` — calls `Channels_GetFullChannel` for metadata, then `Messages_GetHistory` for posts (skips media-only posts with no text)
- `FetchComments()` — calls `Messages_GetReplies` per post; resolves author usernames via `result.UserOrChat()`
- `RetryAsync<T>()` — wraps all API calls; FloodWait (420) sleeps server-supplied seconds, other errors use exponential backoff (2s, 4s, 8s…), default 3 attempts
- `FetchComments()` silently swallows `MSG_ID_INVALID` / `CHAT_ID_INVALID` — these mean a post has no comment section, not a bug
- `ApiThrottle` — 500ms fixed delay injected between every API call to stay under rate limits

Output is written as a single indented JSON to `--output` (default `data/results.json`). The `data/` directory is Docker-volume-mounted so session and output files persist on the host.

`DATA_DIR` env var controls where the session file and default output are written (set to `/data` in Docker, `.` locally).

## Project files

- `Program.cs` — all scraper logic
- `scraper.csproj` — .NET 9 project, depends on `WTelegramClient` and `DotNetEnv`
- `Dockerfile` — multi-stage build: SDK image compiles a self-contained single-file binary, runtime-deps image runs it
- `docker-compose.yml` — mounts `./data` as `/data`, passes `.env`
- `tg-scraper.slnx` — solution file
