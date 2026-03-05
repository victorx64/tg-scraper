import asyncio
import argparse
import json
import sys
from datetime import datetime, timezone

from dotenv import load_dotenv
import os

from telethon import TelegramClient
from telethon.tl.functions.contacts import SearchRequest
from telethon.tl.functions.messages import GetRepliesRequest
from telethon.tl.functions.channels import GetFullChannelRequest
from telethon.errors import FloodWaitError, MsgIdInvalidError, ChatIdInvalidError

load_dotenv()

API_ID = int(os.environ["API_ID"])
API_HASH = os.environ["API_HASH"]
PHONE = os.environ["PHONE"]
DATA_DIR = os.environ.get("DATA_DIR", ".")
SESSION = os.path.join(DATA_DIR, "tg_scraper")


def parse_args():
    parser = argparse.ArgumentParser(
        description="Search Telegram channels and scrape posts + comments to JSON."
    )
    parser.add_argument("query", help="Search query string")
    parser.add_argument("--channels", type=int, default=5, metavar="N",
                        help="Max channels to scrape (default: 5)")
    parser.add_argument("--posts", type=int, default=50, metavar="N",
                        help="Max posts per channel (default: 50)")
    parser.add_argument("--comments", type=int, default=100, metavar="N",
                        help="Max comments per post (default: 100)")
    default_output = os.path.join(DATA_DIR, "results.json")
    parser.add_argument("--output", default=default_output, metavar="FILE",
                        help=f"Output JSON file (default: {default_output})")
    return parser.parse_args()


def log(msg: str):
    print(msg, file=sys.stderr, flush=True)


def serialize_message(msg) -> dict:
    return {
        "id": msg.id,
        "date": msg.date.astimezone(timezone.utc).isoformat(),
        "text": msg.message or "",
        "views": msg.views,
        "forwards": msg.forwards,
        "comments_count": (msg.replies.replies if msg.replies else 0),
    }


async def fetch_comments(client, channel, msg_id: int, limit: int) -> list:
    comments = []
    try:
        result = await client(GetRepliesRequest(
            peer=channel,
            msg_id=msg_id,
            offset_id=0,
            offset_date=None,
            add_offset=0,
            limit=limit,
            max_id=0,
            min_id=0,
            hash=0,
        ))
        for msg in result.messages:
            author = None
            if msg.from_id:
                try:
                    sender = await client.get_entity(msg.from_id)
                    author = getattr(sender, "username", None)
                except Exception:
                    pass
            comments.append({
                "id": msg.id,
                "date": msg.date.astimezone(timezone.utc).isoformat(),
                "text": msg.message or "",
                "author": author,
            })
    except (MsgIdInvalidError, ChatIdInvalidError):
        pass
    except FloodWaitError as e:
        log(f"    FloodWait on comments: sleeping {e.seconds}s")
        await asyncio.sleep(e.seconds)
    return comments


async def fetch_channel_data(client, entity, args) -> dict:
    full_info = await client(GetFullChannelRequest(entity))

    channel_dict = {
        "id": entity.id,
        "username": getattr(entity, "username", None),
        "title": entity.title,
        "description": full_info.full_chat.about or "",
        "members_count": full_info.full_chat.participants_count,
        "posts": [],
    }

    log(f"  Fetching up to {args.posts} posts from @{channel_dict['username'] or entity.id}...")

    async for msg in client.iter_messages(entity, limit=args.posts):
        if not msg.message:
            continue

        post = serialize_message(msg)
        log(f"    Post {msg.id}: fetching comments...")
        post["comments"] = await fetch_comments(client, entity, msg.id, args.comments)
        channel_dict["posts"].append(post)

    return channel_dict


async def search_channels(client, query: str, limit: int) -> list:
    log(f"Searching for channels matching '{query}'...")
    result = await client(SearchRequest(q=query, limit=limit))

    channels = []
    for chat in result.chats:
        if hasattr(chat, "broadcast") or hasattr(chat, "megagroup"):
            channels.append(chat)

    log(f"Found {len(channels)} channel(s).")
    return channels[:limit]


async def main():
    args = parse_args()

    client = TelegramClient(SESSION, API_ID, API_HASH)
    await client.start(phone=PHONE)

    log(f"Authenticated. Starting scrape for: '{args.query}'")

    output = {
        "query": args.query,
        "scraped_at": datetime.now(timezone.utc).isoformat(),
        "channels": [],
    }

    channels = await search_channels(client, args.query, args.channels)

    for i, channel in enumerate(channels, 1):
        log(f"[{i}/{len(channels)}] Scraping: {channel.title}")
        try:
            data = await fetch_channel_data(client, channel, args)
            output["channels"].append(data)
        except FloodWaitError as e:
            log(f"  FloodWait: sleeping {e.seconds}s then retrying...")
            await asyncio.sleep(e.seconds)
            try:
                data = await fetch_channel_data(client, channel, args)
                output["channels"].append(data)
            except Exception as ex:
                log(f"  Failed after retry: {ex}")
        except Exception as e:
            log(f"  Skipping due to error: {e}")

    with open(args.output, "w", encoding="utf-8") as f:
        json.dump(output, f, ensure_ascii=False, indent=2)

    log(f"Done. Results written to {args.output}")

    await client.disconnect()


if __name__ == "__main__":
    asyncio.run(main())
