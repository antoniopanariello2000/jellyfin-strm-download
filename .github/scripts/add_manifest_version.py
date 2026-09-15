#!/usr/bin/env python3
"""Add a released version to the plugin repository manifest.

manifest.json is what Jellyfin fetches when the repository URL is added to a
server, so every released tag has to appear here with the checksum of the
exact ZIP that was uploaded to the release.
"""

import argparse
import datetime
import json

import yaml


def add_version(manifest, entry):
    """Insert entry as the newest version, replacing an existing one."""
    plugin = manifest[0]
    versions = [
        existing
        for existing in plugin.get("versions", [])
        if existing.get("version") != entry["version"]
    ]
    versions.insert(0, entry)
    plugin["versions"] = versions
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", default="manifest.json")
    parser.add_argument("--build-yaml", default="build.yaml")
    parser.add_argument("--version", required=True)
    parser.add_argument("--checksum", required=True)
    parser.add_argument("--source-url", required=True)
    args = parser.parse_args()

    with open(args.build_yaml, encoding="utf-8") as handle:
        build_config = yaml.safe_load(handle)

    with open(args.manifest, encoding="utf-8") as handle:
        manifest = json.load(handle)

    entry = {
        "version": args.version,
        "changelog": str(build_config["changelog"]).strip(),
        "targetAbi": str(build_config["targetAbi"]).strip(),
        "sourceUrl": args.source_url,
        "checksum": args.checksum,
        "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    }

    with open(args.manifest, "w", encoding="utf-8") as handle:
        json.dump(add_version(manifest, entry), handle, indent=4)
        handle.write("\n")

    print(f"added {args.version} to {args.manifest}")


if __name__ == "__main__":
    main()
