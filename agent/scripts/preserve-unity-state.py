"""Snapshot source around Unity runs; restore only known generated file changes."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess


ROOT = Path(__file__).resolve().parents[2]
MUTABLE = (
    "Assets/Fonts/NotoSansCJK-Light SDF.asset",
    "Assets/Generated/ShaderWarmup/OpenBrushBrushVariants.shadervariants",
    "Assets/Generated/ShaderWarmup/open-brush-brush-variant-inventory.json",
    "Assets/Resources/PerformanceTestRunInfo.json",
    "Assets/Resources/PerformanceTestRunInfo.json.meta",
    "Assets/Resources/PerformanceTestRunSettings.json",
    "Assets/Resources/PerformanceTestRunSettings.json.meta",
    "Assets/Resources/UnityGLTFSettings.asset",
    "Assets/Settings/Open Brush Universal Render Pipeline Asset.asset",
    "Assets/UniversalRenderPipelineGlobalSettings.asset",
    "ProjectSettings/ProjectSettings.asset",
    "Assets/Scenes/Main.unity",
    "Assets/Scenes/Loading.unity",
)


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT)


def digest(path):
    if not path.is_file():
        return None
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def project_path(name):
    path = (ROOT / name).resolve()
    if not path.is_relative_to(ROOT) or path == ROOT:
        raise ValueError(f"Path outside project: {name}")
    return path


def save(snapshot):
    snapshot.mkdir(parents=True, exist_ok=False)
    names = set(git("ls-files", "-z").decode().split("\0")) - {""}
    names.update(set(git("ls-files", "--others", "--exclude-standard", "-z").decode().split("\0")) - {""})
    names.update(MUTABLE)
    hashes = {name: digest(project_path(name)) for name in sorted(names)}
    for name in MUTABLE:
        source = project_path(name)
        if source.is_file():
            backup = snapshot / "before" / name
            backup.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, backup)
    (snapshot / "hashes.json").write_text(json.dumps(hashes, indent=2) + "\n")
    (snapshot / "head.txt").write_bytes(git("rev-parse", "HEAD"))
    (snapshot / "status-before.txt").write_bytes(git("status", "--porcelain=v1", "-uall"))
    print(f"Saved {len(hashes)} source hashes and known mutable backups to {snapshot}")


def verify(snapshot, restore):
    hashes = json.loads((snapshot / "hashes.json").read_text())
    if git("rev-parse", "HEAD") != (snapshot / "head.txt").read_bytes():
        raise RuntimeError("HEAD changed since snapshot; inspect before restoration")
    restored = []
    if restore:
        for name in MUTABLE:
            target = project_path(name)
            if digest(target) == hashes[name]:
                continue
            backup = snapshot / "before" / name
            if hashes[name] is not None and digest(backup) != hashes[name]:
                raise RuntimeError(f"Invalid backup: {name}")
            if target.is_file():
                after = snapshot / "after" / name
                after.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(target, after)
            if hashes[name] is None:
                target.unlink(missing_ok=True)
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(backup, target)
            restored.append(name)
    differences = {}
    for name, before in hashes.items():
        after = digest(project_path(name))
        if before != after:
            differences[name] = {"before": before, "after": after}
    status = git("status", "--porcelain=v1", "-uall")
    (snapshot / "status-after.txt").write_bytes(status)
    status_changed = status != (snapshot / "status-before.txt").read_bytes()
    report = {"restored": restored, "differences": differences, "status_changed": status_changed}
    (snapshot / "verification.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))
    if differences or status_changed:
        raise SystemExit("Source or Git status changed; inspect remaining differences")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=("save", "check", "restore"))
    parser.add_argument("snapshot", type=Path, help="A new run-specific directory under agent/logs")
    args = parser.parse_args()
    snapshot = args.snapshot.resolve()
    if not snapshot.is_relative_to(ROOT / "agent" / "logs"):
        parser.error("Snapshot must be under this checkout's agent/logs directory")
    if args.mode == "save":
        save(snapshot)
    else:
        verify(snapshot, restore=args.mode == "restore")
