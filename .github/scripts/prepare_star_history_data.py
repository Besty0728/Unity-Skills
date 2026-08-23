#!/usr/bin/env python3
"""Prepare Star History data for the pinned official renderer."""

from __future__ import annotations

import argparse
import base64
import json
import os
import re
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from pathlib import Path


API_VERSION = "2022-11-28"
USER_AGENT = "Unity-Skills Star History updater"
REPOSITORY_PATTERN = re.compile(
    r"^[A-Za-z0-9](?:[A-Za-z0-9_.-]*[A-Za-z0-9])?/"
    r"[A-Za-z0-9](?:[A-Za-z0-9_.-]*[A-Za-z0-9])?$"
)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--token-env", default="GITHUB_TOKEN")
    return parser.parse_args()


def validate_repository(value: str) -> str:
    repository = value.strip().lower()
    if not REPOSITORY_PATTERN.fullmatch(repository):
        raise ValueError("repository must use GitHub owner/name format")
    return repository


def github_request(url: str, token: str, accept: str) -> tuple[object, str]:
    request = urllib.request.Request(
        url,
        headers={
            "Accept": accept,
            "Authorization": f"Bearer {token}",
            "User-Agent": USER_AGENT,
            "X-GitHub-Api-Version": API_VERSION,
        },
    )
    last_error: Exception | None = None
    for attempt in range(1, 4):
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                payload = json.loads(response.read().decode("utf-8"))
                return payload, response.headers.get("Link", "")
        except (OSError, json.JSONDecodeError, urllib.error.URLError) as exc:
            last_error = exc
            if attempt < 3:
                time.sleep(attempt * 2)
    raise RuntimeError(f"GitHub API request failed for {url}: {last_error}")


def next_link(link_header: str) -> str | None:
    for part in link_header.split(","):
        match = re.match(r'\s*<([^>]+)>;\s*rel="([^"]+)"', part)
        if match and match.group(2) == "next":
            return match.group(1)
    return None


def fetch_metadata(repository: str, token: str) -> dict:
    payload, _ = github_request(
        f"https://api.github.com/repos/{repository}",
        token,
        "application/vnd.github+json",
    )
    if not isinstance(payload, dict):
        raise RuntimeError("GitHub repository metadata was not an object")
    return payload


def fetch_stargazers(repository: str, token: str) -> list[datetime]:
    encoded = urllib.parse.quote(repository, safe="/")
    url: str | None = (
        f"https://api.github.com/repos/{encoded}/stargazers?per_page=100"
    )
    timestamps: list[datetime] = []
    while url:
        payload, link_header = github_request(
            url,
            token,
            "application/vnd.github.star+json",
        )
        if not isinstance(payload, list):
            raise RuntimeError("GitHub stargazers response was not an array")
        for item in payload:
            if not isinstance(item, dict) or not isinstance(item.get("starred_at"), str):
                raise RuntimeError("GitHub stargazers response omitted starred_at")
            timestamps.append(
                datetime.fromisoformat(
                    item["starred_at"].replace("Z", "+00:00")
                ).astimezone(timezone.utc)
            )
        url = next_link(link_header)
    timestamps.sort()
    return timestamps


def download_avatar(url: str) -> str:
    parsed = urllib.parse.urlsplit(url)
    if parsed.scheme != "https" or parsed.hostname != "avatars.githubusercontent.com":
        raise ValueError("GitHub returned an unexpected avatar URL")
    separator = "&" if parsed.query else "?"
    request = urllib.request.Request(
        f"{url}{separator}size=22",
        headers={"User-Agent": USER_AGENT},
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        content_type = response.headers.get_content_type()
        body = response.read(2_000_001)
    if not content_type.startswith("image/") or len(body) > 2_000_000:
        raise ValueError("GitHub avatar response was invalid")
    encoded = base64.b64encode(body).decode("ascii")
    return f"data:{content_type};base64,{encoded}"


def format_record_date(value: datetime) -> str:
    current = value.astimezone(timezone.utc)
    return (
        f"{current.year}/{current.month}/{current.day} "
        f"{current.hour}:{current.minute}:{current.second}"
    )


def build_records(
    created_at: datetime,
    timestamps: list[datetime],
    star_count: int,
) -> list[dict[str, int | str]]:
    weekly: dict[tuple[int, int], tuple[datetime, int]] = {}
    for count, starred_at in enumerate(timestamps, start=1):
        week = starred_at.isocalendar()
        weekly[(week.year, week.week)] = (starred_at, count)

    records: dict[datetime, int] = {created_at: 0}
    for starred_at, count in weekly.values():
        records[starred_at] = count

    now = datetime.now(timezone.utc).replace(microsecond=0)
    records[now] = star_count
    return [
        {"date": format_record_date(recorded_at), "count": count}
        for recorded_at, count in sorted(records.items())
    ]


def main() -> int:
    args = parse_args()
    repository = validate_repository(args.repository)
    token = os.environ.get(args.token_env, "").strip()
    if not token:
        raise SystemExit(f"{args.token_env} is empty")

    metadata: dict = {}
    timestamps: list[datetime] = []
    for attempt in range(1, 4):
        timestamps = fetch_stargazers(repository, token)
        metadata = fetch_metadata(repository, token)
        if len(timestamps) == int(metadata["stargazers_count"]):
            break
        if attempt < 3:
            print("Star count changed while fetching; retrying a consistent snapshot.")
            time.sleep(attempt * 2)
    else:
        raise RuntimeError(
            "GitHub star count kept changing while stargazers were fetched"
        )

    owner = metadata.get("owner")
    if not isinstance(owner, dict):
        raise RuntimeError("GitHub metadata omitted owner")
    created_at = datetime.fromisoformat(
        str(metadata["created_at"]).replace("Z", "+00:00")
    ).astimezone(timezone.utc)
    star_count = int(metadata["stargazers_count"])

    payload = {
        "series": [
            {
                "repository": repository,
                "logo_url": download_avatar(str(owner["avatar_url"])),
                "star_records": build_records(created_at, timestamps, star_count),
            }
        ]
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(payload, ensure_ascii=True, indent=2) + "\n",
        encoding="utf-8",
    )
    print(
        f"Prepared {len(payload['series'][0]['star_records'])} records "
        f"from {star_count} stargazers."
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

# Producer:Betsy
