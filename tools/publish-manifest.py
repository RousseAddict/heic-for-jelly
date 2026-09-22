#!/usr/bin/env python3
"""Records a released version in manifest.json, the plugin repository catalogue.

Jellyfin reads that file as a flat JSON array of packages (see
MediaBrowser.Model/Updates/PackageInfo.cs). Everything describing the plugin is
copied from meta.json so the catalogue can never drift from what is inside the
zip; only the checksum and the download URL come from the release itself.

Re-running the same version replaces its entry rather than appending a second
one, so a re-run of a release workflow is harmless.

Usage:
    tools/publish-manifest.py --version 1.0.0.0 --checksum <md5> --url <zip url>
"""

import argparse
import datetime
import json
import pathlib

REPO = pathlib.Path(__file__).resolve().parent.parent
META = REPO / "src" / "Jellyfin.Plugin.HeicForJelly" / "meta.json"
MANIFEST = REPO / "manifest.json"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--checksum", required=True, help="MD5 of the zip, hex")
    parser.add_argument("--url", required=True, help="direct download URL of the zip")
    args = parser.parse_args()

    meta = json.loads(META.read_text())
    if meta["version"] != args.version:
        raise SystemExit(
            f"meta.json says {meta['version']}, asked to publish {args.version}"
        )

    packages = json.loads(MANIFEST.read_text())
    # The catalogue holds exactly one package; anything else means the file was
    # edited into a shape this script would silently mangle.
    if len(packages) != 1 or packages[0]["guid"] != meta["guid"]:
        raise SystemExit(f"{MANIFEST} is not a single-package catalogue for {meta['guid']}")

    released = {
        "version": args.version,
        "changelog": meta["changelog"],
        "targetAbi": meta["targetAbi"],
        "sourceUrl": args.url,
        "checksum": args.checksum,
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime(
            "%Y-%m-%dT%H:%M:%SZ"
        ),
    }
    older = [v for v in packages[0]["versions"] if v["version"] != args.version]

    MANIFEST.write_text(
        json.dumps(
            [
                {
                    "guid": meta["guid"],
                    "name": meta["name"],
                    "description": meta["description"],
                    "overview": meta["overview"],
                    "owner": meta["owner"],
                    "category": meta["category"],
                    "versions": [released] + older,
                }
            ],
            indent=2,
        )
        + "\n"
    )
    print(f"manifest.json now offers {args.version} (abi {meta['targetAbi']})")


if __name__ == "__main__":
    main()
