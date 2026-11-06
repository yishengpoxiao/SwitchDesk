"""Exclude ignored machine data from corresponding source packages and source IDs."""
from pathlib import Path
import hashlib

# Public SysDVR 6.3 SD setup resources from the locked upstream archive. The
# exefs NSP is a homebrew sysmodule, not a game; do not relax the general NSP rule.
PUBLIC_SETUP_PAYLOADS = {
    "StreamingSetup/SDCard/atmosphere/contents/00FF0000A53BB665/exefs.nsp": {
        "size": 59831, "sha256": "cfd1fb6417739ea8387d2ef4c751c6c496579b650868cf0d5f5c4a01277f96b0"},
    "StreamingSetup/SDCard/atmosphere/contents/00FF0000A53BB665/flags/boot2.flag": {
        "size": 2, "sha256": "eb4bd64f7014f7d42e9d358035802242741b974e8dfcd37c59f9c21ce29d781e"},
    "StreamingSetup/SDCard/atmosphere/contents/00FF0000A53BB665/toolbox.json": {
        "size": 81, "sha256": "f0ebbeed8a7ad461d5933c85308ad3bb60e5b528ddef4bed454de1addfadba0b"},
    "StreamingSetup/SDCard/switch/SysDVR-conf.nro": {
        "size": 7725760, "sha256": "d126422c83831a36bdce1d32b9ef08e37373e1cc963ad52d475e687a2f2381f8"},
}

PRIVATE_DIRECTORIES = {
    "bin", "obj", "__pycache__", ".tools", ".git", "work", "artifacts",
    "validation", "backups", "exports", "consolecache", "phase2", "consolesamples", "vendor", "logs",
}
PRIVATE_SUFFIXES = {
    ".user", ".suo", ".log", ".dmp", ".pdb", ".db", ".db-shm", ".db-wal",
    ".nro", ".nsp", ".nsz", ".xci", ".xcz", ".nca", ".tik", ".cert",
    ".keys", ".pem", ".pfx", ".sav",
}
PRIVATE_NAMES = {
    "settings.json", "savedata", "local-save-inspection.json", "console-inspection.json",
    "catalog-check.json", "credential-check.json",
}


def include_source(path: Path, root: Path) -> bool:
    return _include(path, root, False)


def include_windows_output(path: Path, root: Path) -> bool:
    # Public setup exceptions must match both their precise location and bytes.
    relative = path.relative_to(root).as_posix()
    expected = PUBLIC_SETUP_PAYLOADS.get(relative)
    if expected is not None:
        if path.is_symlink() or not path.is_file() or path.stat().st_size != expected["size"]:
            raise RuntimeError("Missing or mismatched public setup payload: " + relative)
        with path.open("rb") as source:
            digest = hashlib.file_digest(source, "sha256").hexdigest()
        if digest != expected["sha256"]:
            raise RuntimeError("Mismatched public setup payload SHA-256: " + relative)
        return True
    return _include(path, root, True)


def _include(path: Path, root: Path, windows_output: bool) -> bool:
    relative = path.relative_to(root)
    excluded = PRIVATE_DIRECTORIES | ({"library", "downloads"} if windows_output else set())
    if any(part.casefold() in excluded for part in relative.parts[:-1]):
        return False
    name = path.name.casefold()
    if name in PRIVATE_NAMES or name.startswith("local."):
        return False
    if name == ".env" or (name.startswith(".env.") and name != ".env.example"):
        return False
    excluded_suffixes = PRIVATE_SUFFIXES - {".nro"} if windows_output else PRIVATE_SUFFIXES
    return path.suffix.casefold() not in excluded_suffixes
