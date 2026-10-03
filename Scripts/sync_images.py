#!/usr/bin/env python
"""Sync product images from shopee-images/ folders to backend via API."""

from __future__ import annotations

import argparse
import asyncio
import sys
from dataclasses import dataclass, field
from pathlib import Path

import httpx
from rapidfuzz import fuzz, process

IMAGE_EXTENSIONS = {".jpg", ".jpeg", ".png", ".webp"}
CONTENT_TYPE_MAP = {
    ".jpg": "image/jpeg",
    ".jpeg": "image/jpeg",
    ".png": "image/png",
    ".webp": "image/webp",
}
MATCH_THRESHOLD = 70


@dataclass
class UploadRecord:
    sku: str
    product_name: str
    variation: str | None
    filename: str
    score: int


@dataclass
class SyncStats:
    uploaded: list[UploadRecord] = field(default_factory=list)
    skipped: list[tuple[str, str]] = field(default_factory=list)
    unmatched_folders: list[str] = field(default_factory=list)
    unmatched_variations: list[tuple[str, str, str]] = field(default_factory=list)
    errors: list[tuple[str, str]] = field(default_factory=list)


def build_shopee_lookup(products: list[dict]) -> dict[str, list[dict]]:
    lookup: dict[str, list[dict]] = {}
    for p in products:
        for pn in p.get("platformNames", []):
            if pn["platform"].lower() == "shopee":
                name = pn["displayName"]
                lookup.setdefault(name, []).append(p)
    return lookup


def build_name_lookup(products: list[dict]) -> dict[str, list[dict]]:
    lookup: dict[str, list[dict]] = {}
    for p in products:
        lookup.setdefault(p["productName"], []).append(p)
    return lookup


def find_product_group(
    folder_name: str,
    shopee_lookup: dict[str, list[dict]],
    name_lookup: dict[str, list[dict]],
) -> tuple[list[dict], str, int] | None:
    if shopee_lookup:
        result = process.extractOne(
            folder_name, list(shopee_lookup.keys()), scorer=fuzz.token_sort_ratio
        )
        if result and result[1] >= MATCH_THRESHOLD:
            return shopee_lookup[result[0]], result[0], result[1]

    if name_lookup:
        result = process.extractOne(
            folder_name, list(name_lookup.keys()), scorer=fuzz.token_sort_ratio
        )
        if result and result[1] >= MATCH_THRESHOLD:
            return name_lookup[result[0]], result[0], result[1]

    return None


def match_variation(filename_stem: str, products: list[dict]) -> dict | None:
    if len(products) == 1:
        return products[0]

    for p in products:
        var = p.get("productVariation") or ""
        if var and var.lower() == filename_stem.lower():
            return p

    variation_map: dict[str, dict] = {}
    for p in products:
        var = p.get("productVariation") or ""
        if var:
            variation_map[var] = p

    if not variation_map:
        return products[0] if products else None

    result = process.extractOne(
        filename_stem, list(variation_map.keys()), scorer=fuzz.token_sort_ratio
    )
    if result and result[1] >= MATCH_THRESHOLD:
        return variation_map[result[0]]

    return None


def match_single_image(products: list[dict]) -> dict | None:
    for p in products:
        if not p.get("productVariation"):
            return p
    return products[0] if products else None


async def upload_image(
    client: httpx.AsyncClient, api_url: str, product_id: int, image_path: Path
) -> bool:
    ct = CONTENT_TYPE_MAP.get(image_path.suffix.lower(), "application/octet-stream")
    data = image_path.read_bytes()
    resp = await client.post(
        f"{api_url}/products/{product_id}/image",
        files={"image": (image_path.name, data, ct)},
    )
    return resp.status_code == 200


def get_image_folders(base: Path) -> list[Path]:
    return sorted(
        d
        for d in base.iterdir()
        if d.is_dir() and not d.name.startswith(".")
    )


def get_image_files(folder: Path) -> list[Path]:
    return sorted(
        f
        for f in folder.iterdir()
        if f.is_file() and f.suffix.lower() in IMAGE_EXTENSIONS
    )


async def sync_images(api_url: str, base_dir: Path, dry_run: bool) -> SyncStats:
    stats = SyncStats()

    async with httpx.AsyncClient(timeout=30.0) as client:
        resp = await client.get(f"{api_url}/products", params={"activeOnly": "false"})
        resp.raise_for_status()
        products: list[dict] = resp.json()

        shopee_lookup = build_shopee_lookup(products)
        name_lookup = build_name_lookup(products)

        print(
            f"Loaded {len(products)} products "
            f"({len(shopee_lookup)} Shopee names, "
            f"{len(name_lookup)} canonical names)\n"
        )

        for folder in get_image_folders(base_dir):
            match = find_product_group(folder.name, shopee_lookup, name_lookup)
            if not match:
                stats.unmatched_folders.append(folder.name)
                continue

            matched_products, matched_name, score = match
            images = get_image_files(folder)

            if not images:
                stats.skipped.append((folder.name, "no image files"))
                continue

            is_single = len(images) == 1

            for img in images:
                if is_single:
                    product = match_single_image(matched_products)
                else:
                    product = match_variation(img.stem, matched_products)

                if not product:
                    stats.unmatched_variations.append(
                        (folder.name, img.name, matched_name)
                    )
                    continue

                record = UploadRecord(
                    sku=product["sellerSku"],
                    product_name=product["productName"],
                    variation=product.get("productVariation"),
                    filename=img.name,
                    score=score,
                )

                if dry_run:
                    stats.uploaded.append(record)
                    continue

                try:
                    ok = await upload_image(client, api_url, product["id"], img)
                    if ok:
                        stats.uploaded.append(record)
                    else:
                        stats.errors.append((str(img), "upload returned non-200"))
                except Exception as e:
                    stats.errors.append((str(img), str(e)))

    return stats


def print_summary(stats: SyncStats, dry_run: bool) -> None:
    prefix = "[DRY RUN] " if dry_run else ""

    print("\n" + "=" * 60)
    print(f"{prefix}SYNC SUMMARY")
    print("=" * 60)

    action = "Would upload" if dry_run else "Uploaded"
    print(f"\n{action}: {len(stats.uploaded)}")
    for r in stats.uploaded:
        var = f" [{r.variation}]" if r.variation else ""
        print(f"  {r.sku} {r.product_name}{var} <- {r.filename} (score:{r.score})")

    if stats.skipped:
        print(f"\nSkipped (no images): {len(stats.skipped)}")
        for name, reason in stats.skipped:
            print(f"  {name}: {reason}")

    if stats.unmatched_folders:
        print(f"\nUnmatched folders: {len(stats.unmatched_folders)}")
        for name in stats.unmatched_folders:
            print(f"  {name}")

    if stats.unmatched_variations:
        print(f"\nUnmatched variations: {len(stats.unmatched_variations)}")
        for folder, fname, matched in stats.unmatched_variations:
            print(f"  {fname} in '{folder}' (matched product: {matched})")

    if stats.errors:
        print(f"\nErrors: {len(stats.errors)}")
        for path, err in stats.errors:
            print(f"  {path}: {err}")

    total = (
        len(stats.uploaded)
        + len(stats.skipped)
        + len(stats.unmatched_folders)
        + len(stats.unmatched_variations)
        + len(stats.errors)
    )
    print(f"\nTotal items processed: {total}")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Sync Shopee images to backend")
    parser.add_argument(
        "--api-url",
        default="http://localhost:8080",
        help="Backend API base URL (default: http://localhost:8080)",
    )
    parser.add_argument(
        "--dir",
        default="shopee-images",
        help="Image directory (default: shopee-images)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Show what would happen without uploading",
    )
    parser.add_argument(
        "--threshold",
        type=int,
        default=MATCH_THRESHOLD,
        help=f"Fuzzy match threshold 0-100 (default: {MATCH_THRESHOLD})",
    )
    return parser.parse_args()


async def main() -> None:
    args = parse_args()
    global MATCH_THRESHOLD
    MATCH_THRESHOLD = args.threshold

    base_dir = Path(args.dir)
    if not base_dir.is_dir():
        print(f"Error: {base_dir} not found", file=sys.stderr)
        sys.exit(1)

    if args.dry_run:
        print("[DRY RUN MODE]\n")

    stats = await sync_images(args.api_url, base_dir, args.dry_run)
    print_summary(stats, args.dry_run)


if __name__ == "__main__":
    asyncio.run(main())
