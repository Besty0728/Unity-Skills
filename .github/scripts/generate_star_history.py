#!/usr/bin/env python3
"""Generate static Star History SVG charts from GitHub stargazer timestamps."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import math
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter
from html import escape
from pathlib import Path


API_VERSION = "2022-11-28"
USER_AGENT = "Unity-Skills star-history generator"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, help="Repository in owner/name form.")
    parser.add_argument("--output-dir", default="docs", help="Directory for generated SVG files.")
    parser.add_argument("--log-scale", action="store_true", help="Render the y-axis on a log scale.")
    parser.add_argument("--placeholder", action="store_true", help="Write placeholder SVGs without API access.")
    return parser.parse_args()


def token_from_env() -> str:
    return (
        os.environ.get("STAR_HISTORY_TOKEN")
        or os.environ.get("GH_STARGAZERS_TOKEN")
        or os.environ.get("GITHUB_TOKEN")
        or ""
    ).strip()


def github_request(url: str, token: str) -> tuple[object, str]:
    headers = {
        "Accept": "application/vnd.github.star+json",
        "X-GitHub-Api-Version": API_VERSION,
        "User-Agent": USER_AGENT,
    }
    if token:
        headers["Authorization"] = f"Bearer {token}"

    request = urllib.request.Request(url, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            payload = json.loads(response.read().decode("utf-8"))
            return payload, response.headers.get("Link", "")
    except urllib.error.HTTPError as exc:
        body = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"GitHub API request failed ({exc.code}) for {url}: {body}") from exc
    except urllib.error.URLError as exc:
        raise RuntimeError(f"GitHub API request failed for {url}: {exc}") from exc


def parse_next_link(link_header: str) -> str | None:
    if not link_header:
        return None
    for part in link_header.split(","):
        match = re.match(r'\s*<([^>]+)>;\s*rel="([^"]+)"', part)
        if match and match.group(2) == "next":
            return match.group(1)
    return None


def fetch_repo_metadata(repo: str, token: str) -> dict:
    url = f"https://api.github.com/repos/{repo}"
    payload, _ = github_request(url, token)
    if not isinstance(payload, dict):
        raise RuntimeError("Unexpected repository metadata response.")
    return payload


def fetch_stargazer_dates(repo: str, token: str) -> list[dt.date]:
    encoded_repo = urllib.parse.quote(repo, safe="/")
    url = f"https://api.github.com/repos/{encoded_repo}/stargazers?per_page=100"
    dates: list[dt.date] = []

    while url:
        payload, link_header = github_request(url, token)
        if not isinstance(payload, list):
            raise RuntimeError("Unexpected stargazers response.")
        for item in payload:
            if not isinstance(item, dict) or "starred_at" not in item:
                raise RuntimeError(
                    "GitHub response did not include starred_at. "
                    "Use a token that can access the stargazers endpoint."
                )
            dates.append(parse_date(item["starred_at"]))
        url = parse_next_link(link_header)

    return sorted(dates)


def parse_date(value: str) -> dt.date:
    return dt.datetime.fromisoformat(value.replace("Z", "+00:00")).date()


def compact_number(value: int) -> str:
    if value >= 1_000_000:
        return f"{value / 1_000_000:.1f}M".replace(".0M", "M")
    if value >= 1000:
        return f"{value / 1000:.1f}K".replace(".0K", "K")
    return str(value)


def date_to_x(date: dt.date, start: dt.date, end: dt.date, width: int) -> float:
    span = max((end - start).days, 1)
    return ((date - start).days / span) * width


def value_to_y(value: int, max_value: int, height: int, log_scale: bool) -> float:
    max_value = max(max_value, 1)
    if log_scale:
        top = math.log10(max_value + 1)
        return height - (math.log10(value + 1) / top) * height
    return height - (value / max_value) * height


def y_ticks(max_value: int, log_scale: bool) -> list[int]:
    max_value = max(max_value, 1)
    if log_scale:
        ticks = [0]
        power = 0
        while 10**power < max_value:
            ticks.append(10**power)
            power += 1
        ticks.append(max_value)
        return unique_sorted_ticks(ticks)

    raw = [0, max_value]
    magnitude = 10 ** max(len(str(max_value)) - 2, 0)
    step = max(1, math.ceil(max_value / 4 / magnitude) * magnitude)
    raw.extend(range(step, max_value, step))
    return unique_sorted_ticks(raw)


def unique_sorted_ticks(values: list[int]) -> list[int]:
    return sorted(dict.fromkeys(v for v in values if v >= 0))


def x_ticks(start: dt.date, end: dt.date) -> list[dt.date]:
    if (end - start).days <= 550:
        ticks = []
        month = dt.date(start.year, start.month, 1)
        while month <= end:
            ticks.append(month)
            next_month = month.month + 2
            next_year = month.year
            if next_month > 12:
                next_month -= 12
                next_year += 1
            month = dt.date(next_year, next_month, 1)
        return ticks

    ticks = []
    for year in range(start.year, end.year + 1):
        ticks.append(dt.date(year, 1, 1))
    return ticks


def build_points(created_at: dt.date, star_dates: list[dt.date]) -> list[tuple[dt.date, int]]:
    counts_by_day = Counter(star_dates)
    points = [(created_at, 0)]
    total = 0
    for date in sorted(counts_by_day):
        total += counts_by_day[date]
        points.append((date, total))
    return points


def make_path(points: list[tuple[dt.date, int]], start: dt.date, end: dt.date, width: int, height: int, log_scale: bool) -> str:
    max_value = max(value for _, value in points)
    commands = []
    for index, (date, value) in enumerate(points):
        x = date_to_x(date, start, end, width)
        y = value_to_y(value, max_value, height, log_scale)
        command = "M" if index == 0 else "L"
        commands.append(f"{command}{x:.2f},{y:.2f}")
    return " ".join(commands)


def theme_colors(theme: str) -> dict[str, str]:
    if theme == "dark":
        return {
            "background": "#0d1117",
            "foreground": "#f0f6fc",
            "muted": "#8b949e",
            "grid": "#30363d",
            "line": "#ff6b6b",
            "accent": "#3fb950",
            "panel": "#161b22",
        }
    return {
        "background": "#ffffff",
        "foreground": "#24292f",
        "muted": "#57606a",
        "grid": "#d8dee4",
        "line": "#e5534b",
        "accent": "#2da44e",
        "panel": "#f6f8fa",
    }


def render_svg(repo: str, created_at: dt.date, star_dates: list[dt.date], theme: str, log_scale: bool) -> str:
    colors = theme_colors(theme)
    width = 800
    height = 533
    left = 78
    top = 64
    plot_width = 675
    plot_height = 360
    bottom = top + plot_height
    points = build_points(created_at, star_dates)
    start = min(created_at, points[0][0])
    end = points[-1][0]
    total_stars = max(value for _, value in points)
    path = make_path(points, start, end, plot_width, plot_height, log_scale)
    scale_label = "log scale" if log_scale else "linear scale"

    y_axis = []
    for tick in y_ticks(total_stars, log_scale):
        y = top + value_to_y(tick, total_stars, plot_height, log_scale)
        y_axis.append(
            f'<line x1="{left}" y1="{y:.2f}" x2="{left + plot_width}" y2="{y:.2f}" stroke="{colors["grid"]}" stroke-width="1"/>'
            f'<text x="{left - 12}" y="{y + 5:.2f}" text-anchor="end" font-size="14" fill="{colors["muted"]}">{compact_number(tick)}</text>'
        )

    x_axis = []
    for tick in x_ticks(start, end):
        if tick < start or tick > end:
            continue
        x = left + date_to_x(tick, start, end, plot_width)
        label = tick.strftime("%Y") if tick.month == 1 else tick.strftime("%b")
        x_axis.append(
            f'<line x1="{x:.2f}" y1="{top}" x2="{x:.2f}" y2="{bottom}" stroke="{colors["grid"]}" stroke-width="1"/>'
            f'<text x="{x:.2f}" y="{bottom + 28}" text-anchor="middle" font-size="14" fill="{colors["muted"]}">{label}</text>'
        )

    safe_repo = escape(repo)
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" role="img" aria-labelledby="title desc">
  <title id="title">Star History for {safe_repo}</title>
  <desc id="desc">Static GitHub star history chart generated from stargazer timestamps.</desc>
  <rect width="800" height="533" rx="18" fill="{colors["background"]}"/>
  <text x="400" y="36" text-anchor="middle" font-family="Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif" font-size="24" font-weight="700" fill="{colors["foreground"]}">Star History</text>
  <g font-family="Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif">
    <rect x="{left}" y="46" width="290" height="38" rx="8" fill="{colors["panel"]}" stroke="{colors["grid"]}"/>
    <circle cx="{left + 18}" cy="65" r="5" fill="{colors["line"]}"/>
    <text x="{left + 34}" y="70" font-size="15" font-weight="600" fill="{colors["foreground"]}">{safe_repo}</text>
    <text x="{left + 448}" y="70" font-size="15" font-weight="600" fill="{colors["accent"]}" text-anchor="end">{compact_number(total_stars)} stars</text>
    <g>
      {''.join(x_axis)}
      {''.join(y_axis)}
      <line x1="{left}" y1="{bottom}" x2="{left + plot_width}" y2="{bottom}" stroke="{colors["foreground"]}" stroke-width="2"/>
      <line x1="{left}" y1="{top}" x2="{left}" y2="{bottom}" stroke="{colors["foreground"]}" stroke-width="2"/>
      <path d="{path}" transform="translate({left} {top})" fill="none" stroke="{colors["line"]}" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"/>
    </g>
    <text x="400" y="490" text-anchor="middle" font-size="16" font-weight="600" fill="{colors["foreground"]}">Date</text>
    <text x="23" y="244" transform="rotate(-90 23 244)" text-anchor="middle" font-size="16" font-weight="600" fill="{colors["foreground"]}">GitHub Stars</text>
    <text x="400" y="515" text-anchor="middle" font-size="12" fill="{colors["muted"]}">Static snapshot - {scale_label}</text>
  </g>
</svg>
'''


def render_placeholder(repo: str, theme: str) -> str:
    colors = theme_colors(theme)
    safe_repo = escape(repo)
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="800" height="533" viewBox="0 0 800 533" role="img" aria-labelledby="title desc">
  <title id="title">Star History for {safe_repo}</title>
  <desc id="desc">Placeholder chart until the scheduled workflow generates the first static SVG.</desc>
  <rect width="800" height="533" rx="18" fill="{colors["background"]}"/>
  <g font-family="Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, Segoe UI, sans-serif">
    <text x="400" y="190" text-anchor="middle" font-size="28" font-weight="700" fill="{colors["foreground"]}">Star History</text>
    <text x="400" y="232" text-anchor="middle" font-size="18" fill="{colors["muted"]}">{safe_repo}</text>
    <path d="M130 350 C260 310 345 330 455 250 S620 130 690 165" fill="none" stroke="{colors["line"]}" stroke-width="5" stroke-linecap="round"/>
    <text x="400" y="398" text-anchor="middle" font-size="15" fill="{colors["muted"]}">Static chart will update after the scheduled workflow runs.</text>
  </g>
</svg>
'''


def write_svg(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8")
    print(f"Wrote {path}")


def main() -> int:
    args = parse_args()
    output_dir = Path(args.output_dir)

    if args.placeholder:
        for theme in ("light", "dark"):
            write_svg(output_dir / f"star-history-{theme}.svg", render_placeholder(args.repo, theme))
        return 0

    token = token_from_env()
    if not token:
        print(
            "Missing token. Set STAR_HISTORY_TOKEN, GH_STARGAZERS_TOKEN, or GITHUB_TOKEN.",
            file=sys.stderr,
        )
        return 1

    metadata = fetch_repo_metadata(args.repo, token)
    created_at = parse_date(str(metadata["created_at"]))
    star_dates = fetch_stargazer_dates(args.repo, token)
    if not star_dates:
        print("No stargazer timestamps returned.", file=sys.stderr)
        return 1

    for theme in ("light", "dark"):
        svg = render_svg(args.repo, created_at, star_dates, theme, args.log_scale)
        write_svg(output_dir / f"star-history-{theme}.svg", svg)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
