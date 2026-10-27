#!/usr/bin/env python3
"""Collect deterministic third-party notices for the frozen Windows worker.

The collector intentionally fails closed: every pinned Python distribution and
every Conda-owned native runtime copied into the PyInstaller bundle must have a
locally available license file.  This keeps a worker rebuild from silently
shipping a dependency without updating the release notices.
"""

from __future__ import annotations

import argparse
import fnmatch
import hashlib
import importlib.metadata as metadata
import json
import re
import shutil
import sys
from pathlib import Path, PurePosixPath
from typing import Any

from packaging.requirements import Requirement
from packaging.utils import canonicalize_name
from packaging.version import Version


LICENSE_PREFIXES = ("license", "copying", "notice", "copyright")
NATIVE_COMPONENTS = {
    "python": ("python3*.dll",),
    "bzip2": ("libbz2.dll",),
    "expat": ("libexpat.dll",),
    "libffi": ("ffi.dll",),
    "openssl": ("libcrypto-3-*.dll", "libssl-3-*.dll"),
    "xz": ("liblzma.dll",),
    "zlib": ("zlib.dll",),
    "ucrt": ("ucrtbase.dll", "api-ms-win-*.dll"),
    "vc14_runtime": ("vcruntime140*.dll", "msvcp140*.dll"),
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def safe_name(value: str) -> str:
    cleaned = re.sub(r"[^A-Za-z0-9._+-]+", "-", value).strip("-.")
    if not cleaned:
        raise ValueError(f"Cannot form a safe component name from {value!r}")
    return cleaned


def load_lock(path: Path, role: str) -> dict[str, dict[str, Any]]:
    locked: dict[str, dict[str, Any]] = {}
    for line_number, raw_line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
        line = raw_line.split("#", 1)[0].strip()
        if not line:
            continue
        requirement = Requirement(line)
        if requirement.marker is not None and not requirement.marker.evaluate():
            continue
        pins = list(requirement.specifier)
        if len(pins) != 1 or pins[0].operator != "==" or "*" in pins[0].version:
            raise ValueError(f"{path}:{line_number} is not an exact == pin: {line}")
        key = canonicalize_name(requirement.name)
        if key in locked:
            raise ValueError(f"Duplicate requirement in {path}: {requirement.name}")
        locked[key] = {
            "requested_name": requirement.name,
            "expected_version": pins[0].version,
            "roles": [role],
        }
    if not locked:
        raise ValueError(f"No pinned requirements found in {path}")
    return locked


def declared_license(dist: metadata.Distribution) -> str:
    expression = dist.metadata.get("License-Expression")
    if expression:
        return expression.strip()
    value = (dist.metadata.get("License") or "").strip()
    if value and "\n" not in value and len(value) <= 160:
        return value
    classifiers = [
        item.removeprefix("License :: ")
        for item in (dist.metadata.get_all("Classifier") or [])
        if item.startswith("License :: ")
    ]
    return "; ".join(classifiers) if classifiers else "See copied license files"


def project_url(dist: metadata.Distribution) -> str:
    for value in dist.metadata.get_all("Project-URL") or []:
        label, separator, url = value.partition(",")
        if separator and label.strip().lower() in {"homepage", "source", "repository"}:
            return url.strip()
    return (dist.metadata.get("Home-page") or "").strip()


def copy_python_licenses(
    output_root: Path,
    runtime_lock: Path,
    build_lock: Path,
) -> list[dict[str, Any]]:
    combined = load_lock(runtime_lock, "runtime")
    for key, entry in load_lock(build_lock, "build-tool").items():
        if key in combined:
            if entry["expected_version"] != combined[key]["expected_version"]:
                raise ValueError(f"Conflicting pins for {entry['requested_name']}")
            combined[key]["roles"].append("build-tool")
        else:
            combined[key] = entry

    components: list[dict[str, Any]] = []
    for key in sorted(combined):
        lock = combined[key]
        dist = metadata.distribution(lock["requested_name"])
        if Version(dist.version) != Version(lock["expected_version"]):
            raise RuntimeError(
                f"{dist.metadata.get('Name', lock['requested_name'])} is {dist.version}; "
                f"the lock requires {lock['expected_version']}"
            )
        display_name = dist.metadata.get("Name") or lock["requested_name"]
        component_root = output_root / "python" / safe_name(f"{display_name}-{dist.version}")
        copied: list[dict[str, str]] = []
        for item in sorted(dist.files or [], key=lambda value: str(value).lower()):
            posix = PurePosixPath(str(item).replace("\\", "/"))
            if not posix.name.lower().startswith(LICENSE_PREFIXES):
                continue
            if posix.is_absolute() or ".." in posix.parts:
                raise RuntimeError(f"Unsafe installed license path for {display_name}: {item}")
            source = Path(dist.locate_file(item)).resolve()
            if not source.is_file():
                continue
            target = component_root.joinpath(*posix.parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            copied.append(
                {
                    "path": target.relative_to(output_root).as_posix(),
                    "sha256": sha256(target),
                }
            )
        if not copied:
            raise RuntimeError(f"No installed license file found for {display_name} {dist.version}")
        roles = sorted(set(lock["roles"]))
        if canonicalize_name(display_name) == "pyinstaller":
            roles.append("redistributed-bootloader")
        components.append(
            {
                "name": display_name,
                "version": dist.version,
                "roles": sorted(set(roles)),
                "declared_license": declared_license(dist),
                "project_url": project_url(dist),
                "license_files": copied,
            }
        )
    return components


def load_conda_records() -> dict[str, dict[str, Any]]:
    conda_meta = Path(sys.base_prefix) / "conda-meta"
    if not conda_meta.is_dir():
        raise RuntimeError(
            "This worker build copies Conda runtime DLLs, but sys.base_prefix has no conda-meta directory."
        )
    records: dict[str, dict[str, Any]] = {}
    for path in sorted(conda_meta.glob("*.json")):
        record = json.loads(path.read_text(encoding="utf-8"))
        name = canonicalize_name(str(record.get("name", "")))
        if name:
            records[name] = record
    return records


def copy_native_licenses(output_root: Path, worker_directory: Path) -> list[dict[str, Any]]:
    artifacts = sorted(
        {path.name for path in worker_directory.rglob("*") if path.is_file()},
        key=str.lower,
    )
    artifact_lower = [name.lower() for name in artifacts]
    records = load_conda_records()
    components: list[dict[str, Any]] = []
    for package_name, patterns in NATIVE_COMPONENTS.items():
        matched = sorted(
            {
                artifacts[index]
                for index, name in enumerate(artifact_lower)
                if any(fnmatch.fnmatch(name, pattern.lower()) for pattern in patterns)
            },
            key=str.lower,
        )
        if not matched:
            continue
        key = canonicalize_name(package_name)
        if key not in records:
            raise RuntimeError(f"Conda ownership metadata is missing for native component {package_name}")
        record = records[key]
        extracted = Path(str(record.get("extracted_package_dir") or ""))
        license_root = extracted / "info" / "licenses"
        if not license_root.is_dir():
            raise RuntimeError(
                f"Conda package cache has no license directory for {package_name}: {license_root}"
            )
        display = f"{record['name']}-{record['version']}-{record.get('build', '')}".rstrip("-")
        component_root = output_root / "native" / safe_name(display)
        copied: list[dict[str, str]] = []
        for source in sorted(license_root.rglob("*"), key=lambda value: str(value).lower()):
            if not source.is_file():
                continue
            relative = source.relative_to(license_root)
            target = component_root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
            copied.append(
                {
                    "path": target.relative_to(output_root).as_posix(),
                    "sha256": sha256(target),
                }
            )
        if not copied:
            raise RuntimeError(f"No native license file copied for {display}")
        components.append(
            {
                "name": record["name"],
                "version": str(record["version"]),
                "build": str(record.get("build", "")),
                "roles": ["redistributed-native-runtime"],
                "declared_license": str(record.get("license") or "See copied license files"),
                "project_url": str(record.get("url") or ""),
                "bundled_artifacts": matched,
                "license_files": copied,
            }
        )
    if not any(component["name"] == "python" for component in components):
        raise RuntimeError("The worker contains no recognized Python runtime DLL")
    return components


def write_inventory(output_root: Path, components: list[dict[str, Any]]) -> None:
    components.sort(key=lambda item: (item["name"].lower(), item["version"], item["roles"]))
    inventory = {
        "schema_version": 1,
        "python_runtime": {
            "implementation": sys.implementation.name,
            "version": ".".join(str(part) for part in sys.version_info[:3]),
            "platform": sys.platform,
        },
        "components": components,
    }
    inventory_path = output_root / "COMPONENTS.json"
    inventory_path.write_text(
        json.dumps(inventory, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )

    lines = [
        "# SAR Python Worker Third-Party Licenses",
        "",
        "This directory is generated by `Tools/SARPlotWorker/build_worker.ps1` from the exact",
        "Python environment and native Conda runtime used to build the Windows worker.",
        "`COMPONENTS.json` is the machine-readable inventory and includes a SHA-256 digest for",
        "every copied license file. Build-tool entries are included conservatively; PyInstaller's",
        "bootloader is redistributed with the executable.",
        "",
        "The bundled Pillow license contains notices for its statically linked image libraries;",
        "the bundled NumPy license contains notices for OpenBLAS, LAPACK, and the GCC runtime;",
        "the Matplotlib files include the DejaVu and STIX font licenses.",
        "",
        "| Component | Version | Role | Declared license |",
        "| --- | --- | --- | --- |",
    ]
    for component in components:
        license_name = str(component["declared_license"]).replace("|", "\\|")
        roles = ", ".join(component["roles"])
        lines.append(
            f"| {component['name']} | {component['version']} | {roles} | {license_name} |"
        )
    lines.extend(
        [
            "",
            "Portions of this software are copyright © The FreeType Project",
            "(https://freetype.org). All rights reserved.",
            "",
            "These notices are provided for attribution and license compliance; they are not legal advice.",
            "",
        ]
    )
    (output_root / "README.md").write_text("\n".join(lines), encoding="utf-8", newline="\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--runtime-requirements", required=True, type=Path)
    parser.add_argument("--build-requirements", required=True, type=Path)
    parser.add_argument("--worker-directory", required=True, type=Path)
    args = parser.parse_args()

    for path in (args.runtime_requirements, args.build_requirements):
        if not path.is_file():
            parser.error(f"requirements file does not exist: {path}")
    if not args.worker_directory.is_dir():
        parser.error(f"worker directory does not exist: {args.worker_directory}")

    output = args.output.resolve()
    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True)
    components = copy_python_licenses(output, args.runtime_requirements, args.build_requirements)
    components.extend(copy_native_licenses(output, args.worker_directory))
    write_inventory(output, components)
    print(f"Collected {len(components)} licensed components into {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
