"""Acquire pinned offline speech resources; no microphone access or inference.

Run with Python 3.12+ before opening Unity or building the Windows PCVR player.
Only generated CHRISVoice directories are written. Download hashes are mandatory.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import urllib.request
import uuid
import zipfile


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def acquire(root, cache):
    root = root.resolve()
    source = root / "Support/CHRISVoice"
    manifest = json.loads((source / "resources.json").read_text())
    cache.mkdir(parents=True, exist_ok=True)
    outputs = {}
    plugin_dir = root / "Assets/Plugins/CHRISVoice/x86_64"
    speech_dir = root / "Assets/StreamingAssets/CHRISVoice"
    for kind in ("runtime", "model"):
        pin = manifest[kind]
        archive = cache / pin["url"].rsplit("/", 1)[-1]
        if not archive.exists():
            urllib.request.urlretrieve(pin["url"], archive)
        if sha(archive) != pin["sha256"]:
            raise ValueError(f"Hash mismatch for {archive.name}; no resources extracted")
        with zipfile.ZipFile(archive) as zipped:
            for info in zipped.infolist():
                if info.is_dir():
                    continue
                if not info.filename.startswith(pin["prefix"]):
                    raise ValueError("Unexpected archive root")
                relative = info.filename[len(pin["prefix"]):]
                if kind == "runtime":
                    if relative not in ("libvosk.dll", "libgcc_s_seh-1.dll", "libstdc++-6.dll", "libwinpthread-1.dll"):
                        continue
                    destination = plugin_dir / relative
                else:
                    destination = speech_dir / pin["version"] / relative
                destination = destination.resolve()
                allowed = plugin_dir if kind == "runtime" else speech_dir
                if not destination.is_relative_to(allowed.resolve()):
                    raise ValueError("Unsafe archive path")
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(zipped.read(info))
                outputs[destination.relative_to(root).as_posix()] = sha(destination)
                if kind == "runtime":
                    guid = uuid.uuid5(uuid.NAMESPACE_URL, "CHRISVoice/" + relative).hex
                    destination.with_suffix(".dll.meta").write_text(f"""fileFormatVersion: 2
guid: {guid}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: x86_64
        DefaultValueInitialized: true
        OS: Windows
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: x86_64
  userData:
  assetBundleName:
  assetBundleVariant:
""")
    for license_file in sorted((source / "licenses").glob("*.txt")):
        destination = speech_dir / "licenses" / license_file.name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(license_file, destination)
        outputs[destination.relative_to(root).as_posix()] = sha(destination)
    inventory = {"archives": manifest, "files": outputs}
    (speech_dir / "acquisition.json").write_text(json.dumps(inventory, indent=2) + "\n")
    print(f"Verified and prepared {len(outputs)} offline speech files in {root}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--cache-dir", type=Path)
    args = parser.parse_args()
    acquire(args.project_root, args.cache_dir or args.project_root / "Build/CHRISVoiceDownloads")
