"""Compile the production CNMT parser against host crypto and synthetic sources.

Does not need a Switch SDK, console, keys, game package, or network access. SPL
and libnx I/O are doubles; this checks parser/key selection, not device services.
"""
from pathlib import Path
import argparse
import os
import shutil
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cxx", default=os.environ.get("CXX", "g++"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--sanitize", action="store_true")
    parser.add_argument("--verify-regression", action="store_true",
                        help="also verify that restoring the old section-index key bug fails the fixtures")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = (root / (args.output or "artifacts/CnmtTests")).resolve()
    output.mkdir(parents=True, exist_ok=True)
    stub = output / "stub/switchdesk/cnmt.hpp"
    stub.parent.mkdir(parents=True, exist_ok=True)
    stub.write_text('#pragma once\n#include "cnmt_test_support.hpp"\n', encoding="utf-8")
    executable = output / ("cnmt-tests.exe" if os.name == "nt" else "cnmt-tests")
    requested_compiler = str((root / args.cxx).resolve()) if "/" in args.cxx or "\\" in args.cxx else args.cxx
    compiler = shutil.which(requested_compiler) or requested_compiler
    windows = os.name == "nt" or sys.platform in ("msys", "cygwin")
    command = [compiler, "-std=c++20", "-Wall", "-Wextra", "-Wpedantic", "-Werror", "-O1", "-g",
               "-I" + (output / "stub").as_posix(), "-I" + (root / "companion/tests").as_posix(),
               (root / "companion/tests/cnmt_tests.cpp").as_posix(),
               (root / "companion/source/yati/nx/cnmt.cpp").as_posix(), "-o", executable.as_posix()]
    if windows:
        command += ["-lbcrypt"]
    else:
        command += ["-lcrypto"]
    if args.sanitize:
        command += ["-fsanitize=address,undefined", "-fno-omit-frame-pointer"]
    environment = os.environ.copy()
    if windows:
        # GCC helpers need their own runtime DLL directory in this process only.
        environment["PATH"] = str(Path(compiler).resolve().parent) + os.pathsep + environment.get("PATH", "")
    subprocess.run(command, check=True, cwd=root, env=environment)
    subprocess.run([str(executable)], check=True, cwd=output, env=environment)
    if args.verify_regression:
        production = root / "companion/source/yati/nx/cnmt.cpp"
        text = production.read_text(encoding="utf-8")
        before = "decrypted_header.key_area[2].area"
        if text.count(before) != 1:
            raise RuntimeError("Production key selection anchor changed; review the regression mutation")
        mutant_source = output / "cnmt-wrong-slot.cpp"
        mutant_source.write_text(text.replace(before, "decrypted_header.key_area[section_index].area"), encoding="utf-8")
        mutant_executable = output / ("cnmt-wrong-slot.exe" if windows else "cnmt-wrong-slot")
        mutant_command = [str(arg) for arg in command]
        mutant_command[mutant_command.index(production.as_posix())] = mutant_source.as_posix()
        mutant_command[mutant_command.index(executable.as_posix())] = mutant_executable.as_posix()
        subprocess.run(mutant_command, check=True, cwd=root, env=environment)
        result = subprocess.run([str(mutant_executable)], cwd=output, env=environment, capture_output=True)
        (output / "known-regression.log").write_bytes(result.stdout + result.stderr)
        expected_failure = b"Expected successful parse, stage=Pfs0Magic result=a5f9"
        if result.returncode == 0 or expected_failure not in result.stderr:
            raise RuntimeError("Regression fixture failed to catch the old section-index key bug")
        print("Known key-slot regression detected: old code fails the synthetic AES fixture", flush=True)


if __name__ == "__main__":
    main()
