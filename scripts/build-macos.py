"""Build and package the shared CLI for Apple Silicon and Intel macOS.

Runs portable CLI regressions on the host; cross-publishing on Windows is not a
macOS runtime test. Final output is published only after both architectures pass.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import platform
import shutil
import stat
import struct
import subprocess
import sys
import tempfile
import time
import zipfile

ROOT = Path(__file__).resolve().parents[1]
RIDS = {"osx-arm64": 0x0100000C, "osx-x64": 0x01000007}
PROGRAM = "SwitchDesk.Cli"


def run(arguments, root):
    subprocess.run([str(value) for value in arguments], cwd=root, check=True)


def sha256(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def check_macho(path, rid):
    # Apple xnu EXTERNAL_HEADERS/mach-o/loader.h and osfmk/mach/machine.h.
    with path.open("rb") as source:
        header = source.read(32)
    if len(header) != 32:
        raise RuntimeError("Truncated Mach-O executable: " + str(path))
    magic, cpu, _, filetype, count, size, _, _ = struct.unpack("<8I", header)
    if (magic != 0xFEEDFACF or cpu != RIDS[rid] or filetype != 2
            or count == 0 or size < count * 8 or size > path.stat().st_size - 32):
        raise RuntimeError("Invalid Mach-O executable or architecture: " + str(path))


def safe_files(directory):
    for path in sorted(directory.rglob("*")):
        if path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction()):
            raise RuntimeError("Linked build output cannot be packaged: " + str(path))
        if path.is_file():
            yield path


def write_archive(directory, archive_path, epoch):
    stamp = time.gmtime(max(315532800, epoch))[:6]
    with zipfile.ZipFile(archive_path, "x", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in safe_files(directory):
            relative = path.relative_to(directory).as_posix()
            entry = zipfile.ZipInfo("SwitchDesk/" + relative, stamp)
            entry.create_system = 3
            entry.external_attr = (stat.S_IFREG | (0o755 if relative == PROGRAM else 0o644)) << 16
            entry.compress_type = zipfile.ZIP_DEFLATED
            with path.open("rb") as source, archive.open(entry, "w", force_zip64=True) as target:
                shutil.copyfileobj(source, target)
    with zipfile.ZipFile(archive_path) as archive:
        if archive.testzip() is not None or (archive.getinfo("SwitchDesk/" + PROGRAM).external_attr >> 16) & 0o777 != 0o755:
            raise RuntimeError("Mac archive integrity or executable permissions failed")


def runtime_licenses(root, rid, destination):
    project = root / "src/SwitchDesk.Cli"
    config = json.loads((project / f"bin/Release/net10.0/{rid}/{PROGRAM}.runtimeconfig.json").read_text(encoding="utf-8-sig"))
    frameworks = config["runtimeOptions"].get("includedFrameworks", [])
    matches = [row["version"] for row in frameworks if row.get("name") == "Microsoft.NETCore.App"]
    if len(matches) != 1:
        raise RuntimeError("Cannot identify the bundled .NET runtime version")
    assets = json.loads((project / "obj/project.assets.json").read_text(encoding="utf-8-sig"))
    package_root = Path(assets["project"]["restore"]["packagesPath"])
    package = package_root / ("microsoft.netcore.app.runtime." + rid) / matches[0]
    destination.mkdir(parents=True)
    for name in ("LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"):
        shutil.copyfile(package / name, destination / ("dotnet-" + name))
    return matches[0]


def publish(root, dotnet, output):
    root = root.resolve()
    nominal = Path(os.path.abspath(root / output))
    output = nominal.resolve()
    if output != nominal or output == root or not output.is_relative_to(root):
        raise ValueError("Mac output must be an unlinked directory inside the project")
    if output.relative_to(root).parts[0] in {"src", "scripts", "companion", "tests", "docs", "third_party", ".github"}:
        raise ValueError("Mac output cannot be inside a source directory")
    if output.exists():
        raise FileExistsError("Mac output already exists; choose a new output directory")
    if "/" in dotnet or "\\" in dotnet:
        dotnet = str((root / dotnet).resolve())
    output.parent.mkdir(parents=True, exist_ok=True)
    run([sys.executable, "-B", root / "scripts/build-info.py"], root)
    identity = json.loads((root / "build-info.json").read_text(encoding="utf-8-sig"))
    project = root / "src/SwitchDesk.Cli/SwitchDesk.Cli.csproj"
    with tempfile.TemporaryDirectory(prefix=".SwitchDesk-macos-", dir=output.parent) as temporary:
        scratch = Path(temporary)
        host = scratch / "host"
        stage = scratch / "delivery"
        stage.mkdir()
        run([dotnet, "publish", project, "-c", "Release", "--self-contained", "false",
             "-p:UseAppHost=false", "-p:DebugType=None", "-p:DebugSymbols=false", "-o", host, "--nologo"], root)
        run([sys.executable, "-B", root / "tests/test_cli.py", "--dotnet", dotnet,
             "--cli", host / (PROGRAM + ".dll"), "--fixtures", scratch / "cli-fixtures"], root)
        run([dotnet, "run", "--project", root / "tests/SwitchDesk.PortableTests/SwitchDesk.PortableTests.csproj",
             "-c", "Release", "--", scratch / "portable-fixtures"], root)
        manifests = []
        for rid in RIDS:
            target = stage / rid
            run([dotnet, "publish", project, "-c", "Release", "-r", rid, "--self-contained", "true",
                 "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                 "-p:EnableCompressionInSingleFile=false", "-p:DebugType=None", "-p:DebugSymbols=false",
                 "-o", target, "--nologo"], root)
            executable = target / PROGRAM
            check_macho(executable, rid)
            executable.chmod(0o755)
            shutil.copyfile(root / "build-info.json", target / "build-info.json")
            for name in ("LICENSE", "THIRD-PARTY-NOTICES.md"):
                shutil.copyfile(root / name, target / name)
            (target / "docs").mkdir()
            for name in ("BUILD.md", "STATUS.md"):
                shutil.copyfile(root / "docs" / name, target / "docs" / name)
            runtime = runtime_licenses(root, rid, target / "licenses")
            (target / "README.txt").write_text(
                f"SwitchDesk {identity['version']} + {identity['sourceId']} ({rid})\n\n"
                "Run in Terminal: ./SwitchDesk.Cli --help\n"
                "Check source identity: ./SwitchDesk.Cli --version\n"
                "If necessary: chmod +x SwitchDesk.Cli\n"
                "This is the portable CLI, not the Windows desktop/WinUSB application.\n"
                "The .NET runtime is included. See docs/BUILD.md for setup and docs/STATUS.md for validation limits.\n"
                "Matching corresponding source is supplied alongside this archive as SwitchDesk-Source.zip.\n",
                encoding="utf-8")
            machine = platform.machine().lower()
            native = platform.system() == "Darwin" and ((rid == "osx-arm64" and machine in {"arm64", "aarch64"})
                                                        or (rid == "osx-x64" and machine in {"x86_64", "amd64"}))
            if native:
                version = subprocess.check_output([str(executable), "--version"], cwd=root, text=True).strip()
                if version != f"SwitchDesk/{identity['version']}+{identity['sourceId']}":
                    raise RuntimeError("Native macOS executable reports a different source identity")
            name = "SwitchDesk-macOS-" + rid.removeprefix("osx-") + ".zip"
            write_archive(target, stage / name, identity.get("sourceDateEpoch", 315532800))
            manifests.append({"rid": rid, "runtimeVersion": runtime, "file": name,
                              "size": (stage / name).stat().st_size, "sha256": sha256(stage / name),
                              "executableSha256": sha256(executable), "machOHeaderChecked": True,
                              "nativeVersionTested": native})
        # Reject a source edit during publishing rather than mislabelling the binaries.
        run([sys.executable, "-B", root / "scripts/build-info.py"], root)
        if json.loads((root / "build-info.json").read_text(encoding="utf-8-sig")) != identity:
            raise RuntimeError("Source changed during Mac publishing; rebuild both architectures")
        manifest = {"build": identity, "host": platform.system(), "portableCliTestsPassed": True, "portableCoreTestsPassed": True,
                    "hardwareTested": False, "assets": manifests}
        (stage / "macos-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        # Destination creation is exclusive. Never merge with a prior delivery.
        output.mkdir()
        moved = []
        try:
            for path in stage.iterdir():
                path.rename(output / path.name)
                moved.append(path.name)
        except BaseException:
            # Roll back only this invocation's known output; never traverse links.
            for name in reversed(moved):
                (output / name).rename(stage / name)
            output.rmdir()
            raise
    print(json.dumps(manifest, indent=2))
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", required=True, help=".NET SDK executable or command name")
    parser.add_argument("--output", type=Path, default=Path("artifacts/macOS"), help="New output directory, relative to project root")
    args = parser.parse_args()
    publish(ROOT, args.dotnet, args.output)


if __name__ == "__main__":
    main()
