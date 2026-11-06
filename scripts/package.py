"""Package a verified Windows build together with its matching source inputs."""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
import zipfile
from source_inputs import PUBLIC_SETUP_PAYLOADS, include_source, include_windows_output

ROOT = Path(__file__).resolve().parents[1]
ARCHIVES = ("SwitchDesk-Windows-x64.zip", "SwitchDesk-Source.zip", "SHA256SUMS.txt")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def require_unlinked(path: Path, root: Path) -> None:
    """Reject linked files and directory ancestors, including Windows junctions."""
    path.relative_to(root)
    current = path
    while True:
        if current.is_symlink() or (hasattr(current, "is_junction") and current.is_junction()):
            raise RuntimeError("Linked package input cannot be packaged: " + str(path.relative_to(root)))
        if current == root:
            return
        current = current.parent


def validate_setup_manifest(windows: Path) -> None:
    """Reject an incomplete or filtered SD setup before publishing any ZIP."""
    manifest = windows / "StreamingSetup/setup-manifest.json"
    if not manifest.exists():
        if any((windows / relative).exists() for relative in PUBLIC_SETUP_PAYLOADS):
            raise RuntimeError("Missing public setup manifest")
        return
    if manifest.is_symlink() or not manifest.is_file():
        raise RuntimeError("Invalid public setup manifest")
    try:
        data = json.loads(manifest.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError) as error:
        raise RuntimeError("Invalid public setup manifest") from error
    if not isinstance(data, dict) or not isinstance(data.get("files"), list):
        raise RuntimeError("Invalid public setup manifest file list")
    listed = set()
    for entry in data["files"]:
        if not isinstance(entry, dict) or not isinstance(entry.get("path"), str):
            raise RuntimeError("Invalid public setup manifest entry")
        name = entry["path"]
        relative = PurePosixPath(name)
        if (not name or not relative.parts or relative.is_absolute() or relative.as_posix() != name
                or relative.parts[0] != "SDCard" or ".." in relative.parts or "\\" in name
                or ":" in name or "\0" in name):
            raise RuntimeError("Unsafe public setup manifest path")
        key = "StreamingSetup/" + name
        if key.casefold() in listed:
            raise RuntimeError("Duplicate public setup manifest path")
        listed.add(key.casefold())
        length = entry.get("size")
        digest = entry.get("sha256")
        if (type(length) is not int or length < 0 or not isinstance(digest, str) or len(digest) != 64
                or any(c not in "0123456789abcdefABCDEF" for c in digest)):
            raise RuntimeError("Invalid public setup manifest length or SHA-256")
        source = windows / key
        if (source.is_symlink() or not source.resolve().is_relative_to(windows)
                or not source.is_file() or source.stat().st_size != length):
            raise RuntimeError("Missing or mismatched public setup manifest file: " + name)
        if sha256(source) != digest.lower():
            raise RuntimeError("Public setup manifest SHA-256 mismatch: " + name)
        if not include_windows_output(source, windows):
            raise RuntimeError("Public setup manifest file would be excluded from package: " + name)
    if not {name.casefold() for name in PUBLIC_SETUP_PAYLOADS}.issubset(listed):
        raise RuntimeError("Public setup manifest is missing required resources")


def package(root: Path, output: Path, windows: Path | None = None) -> list[dict]:
    root = root.resolve(); output = (root / output).resolve()
    windows = (root / (windows or "artifacts/Windows")).resolve()
    if output.is_relative_to(windows):
        raise ValueError("Package output must be outside the Windows build directory")
    # Never truncate a previous delivery when validation or ZIP generation fails.
    if any((output / name).exists() for name in ARCHIVES):
        raise FileExistsError("Package output already exists; choose a new output directory")
    if not (windows / "SwitchDesk.exe").is_file():
        raise RuntimeError("Build/publish Windows before packaging")
    # Reuse the build's source identity generator rather than maintaining a second algorithm.
    subprocess.run([sys.executable, str(root / "scripts/build-info.py")], check=True)
    current = json.loads((root / "build-info.json").read_text(encoding="utf-8-sig"))
    built_path = windows / "build-info.json"
    if not built_path.is_file() or json.loads(built_path.read_text(encoding="utf-8-sig")) != current:
        raise RuntimeError("Windows build does not match current source; rebuild before packaging")

    directories = ("src", "companion", "scripts", "docs", "tests", "third_party", ".github")
    names = ("README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "Directory.Build.props", "global.json", "SwitchDesk.slnx", ".gitignore", "version.json", "build-info.json", "build-dependencies.lock.json")
    sources = [root / name for name in names]
    for optional in ("README.en.md", "CHANGELOG.md", "CONTRIBUTING.md", "SECURITY.md", "PROGRESS.md", "AGENTS.md", "streaming-dependencies.lock.json"):
        if (root / optional).is_file():
            sources.append(root / optional)
    for directory in directories:
        sources.extend(source for source in (root / directory).rglob("*") if source.is_file() and include_source(source, root))
    lock = json.loads((root / "build-dependencies.lock.json").read_text(encoding="utf-8-sig"))
    # Only locked corresponding sources belong here, never arbitrary ignored vendor files.
    for dependency in lock.get("sources", []):
        name = dependency["file"]
        if Path(name).name != name or "/" in name or "\\" in name:
            raise RuntimeError("Invalid dependency source archive name")
        source = root / "vendor/cache" / name
        if not source.is_file() or sha256(source) != dependency["sha256"].lower():
            raise RuntimeError("Missing or mismatched corresponding dependency source: " + name)
        sources.append(source)
    for source in sources:
        require_unlinked(source, root)
        if not source.is_file():
            raise RuntimeError("Missing source input; no packages were written")
    validate_setup_manifest(windows)

    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".SwitchDesk-package-", dir=output) as temporary:
        stage = Path(temporary)
        with zipfile.ZipFile(stage / ARCHIVES[0], "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for source in sorted(windows.rglob("*")):
                require_unlinked(source, windows)
                if source.is_file() and include_windows_output(source, windows):
                    archive.write(source, "SwitchDesk/" + source.relative_to(windows).as_posix())
        with zipfile.ZipFile(stage / ARCHIVES[1], "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            for source in sorted(set(sources)):
                require_unlinked(source, root)
                archive.write(source, "SwitchDesk/" + source.relative_to(root).as_posix())
        # A build may be followed by concurrent source edits during ZIP creation.
        # Reject this delivery before publishing either archive.
        subprocess.run([sys.executable, str(root / "scripts/build-info.py")], check=True)
        if json.loads((root / "build-info.json").read_text(encoding="utf-8-sig")) != current:
            raise RuntimeError("Source changed during packaging; rebuild before packaging")
        manifest = [{"file": name, "size": (stage / name).stat().st_size, "sha256": sha256(stage / name)} for name in ARCHIVES[:2]]
        (stage / ARCHIVES[2]).write_text(''.join(f"{row['sha256']}  {row['file']}\n" for row in manifest), encoding="utf-8")
        created = []
        try:
            for name in ARCHIVES:
                destination = output / name
                with (stage / name).open("rb") as source, destination.open("xb") as target:
                    created.append(destination)
                    shutil.copyfileobj(source, target)
        except BaseException:
            # A write or close can fail after the exclusive open created the file.
            for destination in reversed(created):
                destination.unlink(missing_ok=True)
            raise
    return manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/Packages",
                        help="Package directory; relative paths start at the project root")
    parser.add_argument("--windows", type=Path, help="Windows output previously created by build.ps1")
    args = parser.parse_args()
    print(json.dumps(package(ROOT, args.output, args.windows), indent=2))


if __name__ == "__main__":
    main()
