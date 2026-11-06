"""Verify same-source CI outputs and assemble the complete development release."""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json
import re
import shutil
import stat
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ARCHIVES = (
    "SwitchDesk-Windows-x64.zip", "SwitchDesk-macOS-arm64.zip",
    "SwitchDesk-macOS-x64.zip", "SwitchDesk-Switch.zip", "SwitchDesk-Source.zip",
)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def match_identity(actual, expected):
    if actual != expected:
        raise ValueError("Release input has a different build identity")


def inspect_archive(path, expected, required):
    with zipfile.ZipFile(path) as archive:
        names = set()
        for item in archive.infolist():
            name = item.filename.rstrip("/")
            parts = PurePosixPath(name)
            if (not name or parts.is_absolute() or parts.as_posix() != name
                    or parts.parts[0] != "SwitchDesk" or ".." in parts.parts
                    or "\\" in name or ":" in name or "\0" in name
                    or stat.S_ISLNK(item.external_attr >> 16) or name.casefold() in names):
                raise ValueError("Unsafe or duplicate release ZIP entry")
            names.add(name.casefold())
        if archive.testzip() is not None:
            raise ValueError("Release ZIP integrity check failed")
        match_identity(json.loads(archive.read("SwitchDesk/build-info.json")), expected)
        for name in required:
            archive.getinfo("SwitchDesk/" + name)


def check_sources(path, root):
    """Require the locked corresponding sources, including transitive archives."""
    lock = read_json(root / "build-dependencies.lock.json")
    archives = {"vendor/cache/" + row["file"]: row["sha256"] for row in lock["sources"]}
    for name in ("libnx", "newlib"):
        archives[lock[name]["archive"]] = lock[name]["sha256"]
    with zipfile.ZipFile(path) as archive:
        # The original Sphaira source and its license are shipped in third_party.
        for source in (root / "third_party").glob("*-source.*"):
            archives[source.relative_to(root).as_posix()] = digest(source)
        if json.loads(archive.read("SwitchDesk/build-dependencies.lock.json")) != lock:
            raise ValueError("Corresponding source dependency lock differs")
        for name, expected in archives.items():
            with archive.open("SwitchDesk/" + name) as stream:
                if hashlib.file_digest(stream, "sha256").hexdigest() != expected.lower():
                    raise ValueError("Missing or mismatched corresponding source: " + name)


def assemble(root, inputs, output, commit):
    root = root.resolve()
    inputs = (root / inputs).resolve()
    output = (root / output).resolve()
    if output.exists():
        raise FileExistsError("Release output already exists; choose a new directory")
    if not output.is_relative_to(root) or output == root or output.is_relative_to(inputs):
        raise ValueError("Release output must be inside the project and outside its inputs")
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Release requires the full Git commit SHA")
    identity = read_json(root / "build-info.json")
    if (not re.fullmatch(r"\d+\.\d+\.\d+-(?:dev|alpha|beta|rc)\.\d+", identity["version"])
            or not re.fullmatch(r"[0-9a-f]{64}", identity["sourceSha256"])
            or identity["sourceId"] != identity["sourceSha256"][:12]):
        raise ValueError("Invalid development build identity")
    windows = inputs / "release-windows"
    winzip = windows / ARCHIVES[0]
    sourcezip = windows / ARCHIVES[4]
    checksums = (windows / "SHA256SUMS.txt").read_text(encoding="utf-8").splitlines()
    expected_sums = [f"{digest(path)}  {path.name}" for path in (winzip, sourcezip)]
    if checksums != expected_sums:
        raise ValueError("Windows/source package checksums differ")
    inspect_archive(winzip, identity, ("SwitchDesk.exe", "LICENSE", "THIRD-PARTY-NOTICES.md"))
    inspect_archive(sourcezip, identity, ("version.json", "scripts/build-info.py", "LICENSE",
                                       ".github/workflows/release.yml", "THIRD-PARTY-NOTICES.md"))
    check_sources(sourcezip, root)
    packages = [winzip, sourcezip]
    mac_results = []
    for arch in ("arm64", "x64"):
        directory = inputs / ("release-macos-" + arch)
        manifest = read_json(directory / "macos-manifest.json")
        match_identity(manifest["build"], identity)
        if (manifest.get("host") != "Darwin" or manifest.get("portableCliTestsPassed") is not True
                or manifest.get("portableCoreTestsPassed") is not True):
            raise ValueError("Mac host tests were not completed")
        rows = [row for row in manifest["assets"] if row["rid"] == "osx-" + arch]
        name = "SwitchDesk-macOS-" + arch + ".zip"
        if (len(rows) != 1 or rows[0].get("nativeVersionTested") is not True
                or rows[0].get("machOHeaderChecked") is not True or rows[0]["file"] != name):
            raise ValueError("Mac architecture was not checked on its native host")
        package = directory / name
        row = rows[0]
        if package.stat().st_size != row["size"] or digest(package) != row["sha256"]:
            raise ValueError("Mac package checksum differs")
        inspect_archive(package, identity, ("SwitchDesk.Cli", "LICENSE",
                                            "licenses/dotnet-LICENSE.TXT", "licenses/dotnet-THIRD-PARTY-NOTICES.TXT"))
        with zipfile.ZipFile(package) as archive:
            executable = archive.getinfo("SwitchDesk/SwitchDesk.Cli")
            if stat.S_IMODE(executable.external_attr >> 16) != 0o755:
                raise ValueError("Mac executable permission is missing")
            with archive.open(executable) as stream:
                if hashlib.file_digest(stream, "sha256").hexdigest() != row["executableSha256"]:
                    raise ValueError("Mac executable checksum differs")
        packages.append(package)
        mac_results.append({"rid": row["rid"], "nativeVersionTested": True,
                            "runtimeVersion": row["runtimeVersion"]})
    companion = inputs / "release-companion"
    match_identity(read_json(companion / "build-info.json"), identity)
    nro = companion / "switch/SwitchDesk/SwitchDesk.nro"
    with nro.open("rb") as stream:
        header = stream.read(0x20)
    if len(header) != 0x20 or header[0x10:0x14] != b"NRO0":
        raise ValueError("Companion output is not an NRO")

    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".SwitchDesk-release-", dir=output.parent) as temporary:
        stage = Path(temporary)
        for source in packages:
            shutil.copyfile(source, stage / source.name)
        with zipfile.ZipFile(stage / ARCHIVES[3], "x", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
            archive.write(nro, "SwitchDesk/switch/SwitchDesk/SwitchDesk.nro")
            for name in ("build-info.json", "LICENSE", "THIRD-PARTY-NOTICES.md", "docs/BUILD.md", "docs/STATUS.md"):
                archive.write(root / name, "SwitchDesk/" + name)
            licenses = root / "third_party/licenses"
            for source in sorted(licenses.rglob("*")):
                if source.is_file():
                    archive.write(source, "SwitchDesk/licenses/" + source.relative_to(licenses).as_posix())
        shutil.copyfile(root / "build-info.json", stage / "build-info.json")
        assets = [{"file": name, "size": (stage / name).stat().st_size, "sha256": digest(stage / name)} for name in ARCHIVES]
        manifest = {"build": identity, "commit": commit, "macOS": mac_results,
                    "hardwareTested": False, "assets": assets}
        (stage / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
        checked = (*ARCHIVES, "build-info.json", "release-manifest.json")
        (stage / "SHA256SUMS.txt").write_text("".join(f"{digest(stage / name)}  {name}\n" for name in checked), encoding="utf-8")
        (stage / "RELEASE-NOTES.md").write_text(
            f"SwitchDesk **{identity['version']}** · 一生破晓\n\n"
            "基于 [Humboldt-Zh/SwitchDesk](https://github.com/Humboldt-Zh/SwitchDesk) 改进，保留原项目与第三方许可。\n\n"
            "修复 USB／FTP 长文件名下载、取消后迟到回复造成的 FTP 连接失步，以及依赖下载中断后的缓存重试；"
            "资源搜索新增固定取消入口，长错误提示不再挤占结果列表。\n\n"
            "包含 Windows x64、macOS Apple Silicon / Intel CLI、Switch 配套、完整对应源码和 SHA256SUMS.txt。"
            "下载后先核对摘要；Windows 解压运行 SwitchDesk.exe，Mac 在终端使用 SwitchDesk.Cli --help。\n\n"
            "本轮通过自动软件检查与目标构建；未进行真机验收。迁移准入仍为空，原生 HOME 隐藏关闭。"
            "Switch 配套不自动部署，具体限制见源码包 docs/STATUS.md。\n\n"
            f"来源：`{identity['sourceId']}`；提交：`{commit}`。\n", encoding="utf-8")
        # Rename the completed directory only after every input has passed.
        stage.rename(output)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inputs", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--commit", required=True)
    args = parser.parse_args()
    print(json.dumps(assemble(ROOT, args.inputs, args.output, args.commit), indent=2))


if __name__ == "__main__":
    main()
