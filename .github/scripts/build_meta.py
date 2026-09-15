#!/usr/bin/env python3
"""Generate the meta.json that ships inside the plugin package.

Jellyfin reads meta.json from the installed plugin folder into
MediaBrowser.Common.Plugins.PluginManifest, whose properties carry explicit
[JsonPropertyName] attributes ("guid", "name", "targetAbi", ...). The shape
produced here matches those names and the output of jprm, the reference
Jellyfin plugin packaging tool.

Everything except the version is taken from build.yaml, so the packaged
metadata cannot drift away from the repository manifest.
"""

import argparse
import datetime
import json

import yaml

# Keys copied verbatim from build.yaml, in jprm's order.
COPIED_KEYS = (
    "guid",
    "name",
    "description",
    "overview",
    "owner",
    "category",
    "changelog",
    "targetAbi",
)

# Optional keys, only emitted when build.yaml defines them.
OPTIONAL_KEYS = ("imageUrl",)


def build_meta(build_config, version, timestamp):
    """Build the meta.json content for one version."""
    meta = {key: str(build_config[key]).strip() for key in COPIED_KEYS}
    meta["version"] = version
    meta["timestamp"] = timestamp

    for key in OPTIONAL_KEYS:
        if build_config.get(key):
            meta[key] = str(build_config[key]).strip()

    # jprm records the packaged logo under "image"; keep that for tooling
    # compatibility. Jellyfin itself resolves the logo from imageUrl.
    if "logo.png" in build_config.get("artifacts", []):
        meta["image"] = "logo.png"

    return meta


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build-yaml", default="build.yaml")
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()

    with open(args.build_yaml, encoding="utf-8") as handle:
        build_config = yaml.safe_load(handle)

    timestamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    meta = build_meta(build_config, args.version, timestamp)

    with open(args.output, "w", encoding="utf-8") as handle:
        json.dump(meta, handle, sort_keys=True, indent=4)
        handle.write("\n")

    print(f"wrote {args.output} for version {args.version}")


if __name__ == "__main__":
    main()
