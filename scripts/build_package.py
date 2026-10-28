#!/usr/bin/env python3
"""
APTV Windows Packaging, Architecture PE Verification, and Integrity Manifest Generator.
Builds self-contained win-x64 and win-arm64 packages, isolates VideoLAN 3.0.24 native DLLs,
verifies PE machine headers (0x8664 vs 0xaa64) and CLR headers, and creates candidate distribution ZIPs.
Standard library only.
"""

import argparse
import hashlib
import json
import os
import shutil
import struct
import subprocess
import sys
import zipfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARTIFACTS_DIR = os.path.join(REPO_ROOT, "artifacts")
DESKTOP_CSPROJ = os.path.join(REPO_ROOT, "src", "Iptv.Desktop", "Iptv.Desktop.csproj")
ALLOWED_RIDS = ("win-x64", "win-arm64")
KNOWN_CROSS_ARCH_DIRS = {"win-x86", "win-arm64", "win-x64"}


class SecurityError(RuntimeError):
    """Raised when an unsafe path or escape is detected."""
    pass


def sha256_file(filepath):
    h = hashlib.sha256()
    with open(filepath, "rb") as f:
        while chunk := f.read(65536):
            h.update(chunk)
    return h.hexdigest()


def get_git_commit():
    try:
        res = subprocess.run(
            ["git", "rev-parse", "HEAD"],
            cwd=REPO_ROOT,
            capture_output=True,
            text=True,
            check=True
        )
        return res.stdout.strip()
    except Exception:
        return "unknown"


def get_code_snapshot_commit():
    """
    Extracts the exact Git source revision embedded into the compiled binary by .NET SDK
    via AssemblyInformationalVersionAttribute in obj/Release.
    Falls back to current git commit if obj metadata is not yet generated.
    """
    import re
    info_path = os.path.join(REPO_ROOT, "src", "Iptv.Desktop", "obj", "Release", "net10.0", "win-x64", "Iptv.Desktop.AssemblyInfo.cs")
    if os.path.isfile(info_path):
        with open(info_path, "r", encoding="utf-8") as f:
            content = f.read()
        m = re.search(r'AssemblyInformationalVersionAttribute\("[^"]*\+([a-f0-9]+)"\)', content)
        if m:
            return m.group(1)
    return get_git_commit()


def resolve_dotnet():
    """Resolves dotnet executable without relying on or modifying global PATH."""
    if os.environ.get("DOTNET"):
        cand = os.environ["DOTNET"]
        if os.path.isfile(cand):
            return cand

    exe_name = "dotnet.exe" if sys.platform.startswith("win") else "dotnet"

    # 1. Project private toolchain
    p_local = os.path.join(REPO_ROOT, ".tools", "dotnet", exe_name)
    if os.path.isfile(p_local):
        return p_local

    # 2. DOTNET_ROOT environment variable
    if os.environ.get("DOTNET_ROOT"):
        p_root = os.path.join(os.environ["DOTNET_ROOT"], exe_name)
        if os.path.isfile(p_root):
            return p_root

    # 3. Safe fallback lookup
    which_p = shutil.which(exe_name)
    if which_p:
        return which_p

    raise RuntimeError(f"Cannot resolve dotnet executable ({exe_name}) in private toolchain!")


def parse_pe_info(filepath):
    """
    Parses DOS MZ header, COFF header, Optional Header, Section table,
    and IMAGE_COR20_HEADER (CLI/CLR metadata directory).
    Accurately classifies NATIVE_PE vs MANAGED_IL_ANYCPU vs MANAGED_R2R_TARGET.
    """
    with open(filepath, "rb") as f:
        dos = f.read(64)
        if len(dos) < 64 or dos[:2] != b"MZ":
            return {"is_pe": False, "error": "Missing DOS MZ signature"}
        pe_offset = struct.unpack_from("<I", dos, 0x3C)[0]

        f.seek(pe_offset)
        sig = f.read(4)
        if sig != b"PE\0\0":
            return {"is_pe": False, "error": "Invalid PE signature"}

        coff = f.read(20)
        if len(coff) < 20:
            return {"is_pe": False, "error": "Truncated COFF header"}
        machine, num_sections = struct.unpack_from("<HH", coff, 0)
        opt_size = struct.unpack_from("<H", coff, 16)[0]

        is_clr_managed = False
        is_r2r = False
        is_32bit_required = False
        is_il_only = False
        cor_error = None
        opt_magic = None

        if opt_size > 0:
            opt = f.read(opt_size)
            if len(opt) >= 2:
                opt_magic = struct.unpack_from("<H", opt, 0)[0]
            clr_offset = 224 if opt_magic == 0x20B else 208
            if len(opt) >= clr_offset + 8:
                clr_rva, clr_size = struct.unpack_from("<II", opt, clr_offset)
                if clr_rva != 0 and clr_size != 0:
                    is_clr_managed = True
                    # Parse Section headers to locate file offset for CLR RVA
                    f.seek(pe_offset + 24 + opt_size)
                    sections = []
                    for _ in range(num_sections):
                        sec = f.read(40)
                        if len(sec) < 40:
                            break
                        vsize, s_rva, raw_size, raw_offset = struct.unpack_from("<IIII", sec, 8)
                        sections.append((s_rva, vsize, raw_offset, raw_size))

                    cor_offset = None
                    for s_rva, s_vsize, s_offset, s_raw_size in sections:
                        if s_rva <= clr_rva < s_rva + max(s_vsize, s_raw_size):
                            cor_offset = s_offset + (clr_rva - s_rva)
                            break

                    if cor_offset is None:
                        cor_error = f"CLR RVA 0x{clr_rva:x} could not be mapped to any PE section"
                    else:
                        f.seek(cor_offset)
                        cor_hdr = f.read(72)
                        if len(cor_hdr) < 72:
                            cor_error = "Truncated IMAGE_COR20_HEADER (< 72 bytes)"
                        else:
                            cb, major, minor, md_rva, md_size, flags, entry, res_rva, res_size, sn_rva, sn_size, cm_rva, cm_size, vt_rva, vt_size, eat_rva, eat_size, r2r_rva, r2r_size = struct.unpack("<IHHIIIIIIIIIIIIIIII", cor_hdr)
                            # Flags: COMIMAGE_FLAGS_ILONLY (0x1), COMIMAGE_FLAGS_32BITREQUIRED (0x2)
                            is_il_only = bool(flags & 0x1)
                            is_32bit_required = bool(flags & 0x2)
                            is_r2r = bool(r2r_rva != 0 and r2r_size != 0)

        machine_name = {
            0x8664: "IMAGE_FILE_MACHINE_AMD64 (x86_64)",
            0xAA64: "IMAGE_FILE_MACHINE_ARM64 (aarch64)",
            0x014C: "IMAGE_FILE_MACHINE_I386 (x86)",
        }.get(machine, f"Unknown (0x{machine:04x})")

        classification = "NATIVE_PE"
        if is_clr_managed:
            if machine == 0x014C and is_il_only and not is_32bit_required and not is_r2r:
                classification = "MANAGED_IL_ANYCPU"
            elif is_r2r:
                classification = "MANAGED_R2R_TARGET"
            elif machine in (0x8664, 0xAA64):
                classification = "MANAGED_TARGET_SPECIFIC"
            else:
                classification = "MANAGED_INVALID"

        return {
            "is_pe": True,
            "machine": machine,
            "machine_hex": f"0x{machine:04x}",
            "machine_name": machine_name,
            "is_clr_managed": is_clr_managed,
            "is_native": not is_clr_managed,
            "is_il_only": is_il_only,
            "is_r2r": is_r2r,
            "is_32bit_required": is_32bit_required,
            "cor_error": cor_error,
            "classification": classification
        }


def safe_prune_cross_arch(out_dir, rid):
    """
    Safely prunes cross-architecture directories under libvlc/.
    Strictly verifies absolute canonical paths, enforces RID whitelist,
    and rejects symlinks and directory escapes.
    """
    if rid not in ALLOWED_RIDS:
        raise SecurityError(f"RID '{rid}' not in allowed whitelist: {ALLOWED_RIDS}")

    abs_out_dir = os.path.realpath(out_dir)
    vlc_dir = os.path.join(out_dir, "libvlc")
    if not os.path.isdir(vlc_dir):
        return

    abs_vlc_dir = os.path.realpath(vlc_dir)
    if not abs_vlc_dir.startswith(abs_out_dir + os.sep):
        raise SecurityError(f"Directory escape detected: {abs_vlc_dir} is outside {abs_out_dir}")

    for sub in os.listdir(vlc_dir):
        sub_path = os.path.join(vlc_dir, sub)
        if os.path.islink(sub_path):
            raise SecurityError(f"Symlink detected in artifact directory: {sub_path}")

        abs_sub_path = os.path.realpath(sub_path)
        if not abs_sub_path.startswith(abs_vlc_dir + os.sep):
            raise SecurityError(f"Directory escape detected in sub-path: {abs_sub_path}")

        if os.path.isdir(sub_path) and sub in KNOWN_CROSS_ARCH_DIRS and sub != rid:
            print(f"Pruning cross-architecture directory from {rid} package: libvlc/{sub}")
            shutil.rmtree(abs_sub_path)


def safe_copy_licenses(out_dir):
    """
    Safely copies project legal documents into target directory.
    Strictly verifies canonical paths, enforces commonpath containment within out_dir,
    rejects symlinks at target paths, and prevents symlink traversal deletion.
    """
    abs_out_dir = os.path.realpath(out_dir)

    for doc in ["README.md", "THIRD-PARTY-NOTICES.md"]:
        src = os.path.join(REPO_ROOT, doc)
        if os.path.isfile(src):
            dst = os.path.join(out_dir, doc)
            if os.path.islink(dst):
                raise SecurityError(f"Symlink detected at document copy target: {dst}")
            abs_dst = os.path.realpath(dst) if os.path.exists(dst) else os.path.realpath(os.path.dirname(dst))
            if os.path.commonpath([abs_out_dir, abs_dst]) != abs_out_dir:
                raise SecurityError(f"Copy target escape: {dst} is outside {abs_out_dir}")
            if not abs_dst.startswith(abs_out_dir + os.sep) and abs_dst != abs_out_dir:
                raise SecurityError(f"Copy target boundary violation: {dst}")
            shutil.copy2(src, dst)

    licenses_src = os.path.join(REPO_ROOT, "LICENSES")
    if os.path.isdir(licenses_src):
        licenses_dst = os.path.join(out_dir, "LICENSES")
        expected_licenses_dst = os.path.join(abs_out_dir, "LICENSES")

        if os.path.islink(licenses_dst):
            raise SecurityError(f"Symlink detected at licenses target path: {licenses_dst}")

        if os.path.exists(licenses_dst):
            abs_lic_dst = os.path.realpath(licenses_dst)
            # Strict identity and containment check: must resolve exactly to expected out_dir/LICENSES
            if abs_lic_dst != os.path.realpath(expected_licenses_dst):
                raise SecurityError(f"Licenses target target mismatch: {abs_lic_dst} != {expected_licenses_dst}")
            if os.path.commonpath([abs_out_dir, abs_lic_dst]) != abs_out_dir:
                raise SecurityError(f"Licenses target escape: {abs_lic_dst} is outside {abs_out_dir}")
            if not abs_lic_dst.startswith(abs_out_dir + os.sep):
                raise SecurityError(f"Licenses target boundary violation: {abs_lic_dst}")
            shutil.rmtree(abs_lic_dst)

        shutil.copytree(licenses_src, licenses_dst)


def publish_rid(rid, expected_machine_hex):
    out_dir = os.path.join(ARTIFACTS_DIR, rid)
    print(f"\n=======================================================")
    print(f"Publishing RID: {rid} (Expected Native PE: {expected_machine_hex})")
    print(f"Output Directory: {out_dir}")
    print(f"=======================================================")

    dotnet_bin = resolve_dotnet()

    env = os.environ.copy()
    env["DOTNET_CLI_HOME"] = os.path.join(REPO_ROOT, ".tools")
    env["NUGET_PACKAGES"] = os.path.join(REPO_ROOT, ".tools", "nuget")
    env["DOTNET_ROOT"] = os.path.dirname(dotnet_bin)
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_MULTILEVEL_LOOKUP"] = "0"

    cmd = [
        dotnet_bin,
        "publish",
        DESKTOP_CSPROJ,
        "-c", "Release",
        "-r", rid,
        "--self-contained", "true",
        "-p:PublishSingleFile=false",
        "-o", out_dir
    ]

    subprocess.run(cmd, env=env, check=True)

    # Clean up mixed architecture directories
    safe_prune_cross_arch(out_dir, rid)

    # Bundle legal documentation
    safe_copy_licenses(out_dir)

    # Audit all files in out_dir
    audit_results = audit_published_directory(out_dir, expected_machine_hex)
    return audit_results


def classify_non_pe_file(rel_path):
    ext = os.path.splitext(rel_path)[1].lower()
    fname = os.path.basename(rel_path).lower()

    if fname.endswith(".runtimeconfig.json"):
        return "RUNTIME_CONFIG"
    elif fname.endswith(".deps.json"):
        return "DEPENDENCY_MANIFEST"
    elif ext in (".json",):
        return "CONFIG_METADATA"
    elif ext in (".pdb",):
        return "DEBUG_SYMBOLS"
    elif ext in (".txt", ".md") or "licenses" in rel_path.lower() or "notices" in fname:
        return "LEGAL_NOTICE"
    elif ext in (".png", ".ico", ".svg", ".jpg"):
        return "GRAPHIC_ASSET"
    elif ext in (".lua", ".luac"):
        return "LUA_SCRIPT"
    elif ext in (".hrtf",):
        return "AUDIO_DATA"
    elif ext in (".lib", ".a"):
        return "STATIC_IMPORT_LIB"
    elif ext in (".h", ".hpp"):
        return "HEADER_FILE"
    else:
        return "PACKAGE_DATA_FILE"


def audit_published_directory(pkg_dir, expected_machine_hex):
    native_count = 0
    il_anycpu_count = 0
    managed_target_count = 0
    r2r_count = 0
    other_files_count = 0
    mismatches = []
    file_records = []
    exp_machine_int = int(expected_machine_hex, 16)

    for root, _, files in sorted(os.walk(pkg_dir)):
        for f in sorted(files):
            fpath = os.path.join(root, f)
            rel = os.path.relpath(fpath, pkg_dir).replace("\\", "/")
            ext = os.path.splitext(f)[1].lower()
            size = os.path.getsize(fpath)
            sha = sha256_file(fpath)

            if ext in (".dll", ".exe"):
                pe = parse_pe_info(fpath)
                if not pe.get("is_pe"):
                    mismatches.append({
                        "file": rel,
                        "type": "CORRUPT_OR_NON_PE",
                        "error": pe.get("error", "Not a valid PE binary")
                    })
                    continue

                if pe.get("cor_error"):
                    mismatches.append({
                        "file": rel,
                        "type": "CORRUPT_CLR_HEADER",
                        "error": pe["cor_error"]
                    })
                    continue

                if pe.get("is_32bit_required"):
                    mismatches.append({
                        "file": rel,
                        "type": "32BIT_REQUIRED_NOT_PERMITTED",
                        "error": "32-bit required assembly in 64-bit distribution package"
                    })
                    continue

                is_native = pe.get("is_native")
                machine = pe.get("machine")
                machine_hex = pe.get("machine_hex")
                is_r2r = pe.get("is_r2r")
                classification = pe.get("classification")

                if is_native:
                    native_count += 1
                    if machine != exp_machine_int:
                        mismatches.append({
                            "file": rel,
                            "type": "NATIVE_PE_MISMATCH",
                            "expected": expected_machine_hex,
                            "actual": machine_hex
                        })
                else:
                    if classification == "MANAGED_IL_ANYCPU":
                        il_anycpu_count += 1
                    elif classification == "MANAGED_R2R_TARGET":
                        managed_target_count += 1
                        r2r_count += 1
                        if machine != exp_machine_int:
                            mismatches.append({
                                "file": rel,
                                "type": "R2R_TARGET_MISMATCH",
                                "expected": expected_machine_hex,
                                "actual": machine_hex
                            })
                    elif classification == "MANAGED_TARGET_SPECIFIC":
                        managed_target_count += 1
                        if machine != exp_machine_int:
                            mismatches.append({
                                "file": rel,
                                "type": "MANAGED_TARGET_MISMATCH",
                                "expected": expected_machine_hex,
                                "actual": machine_hex
                            })
                    else:
                        mismatches.append({
                            "file": rel,
                            "type": "INVALID_MANAGED_CONFIGURATION",
                            "error": f"Machine {machine_hex} cannot be verified as neutral IL or target R2R"
                        })

                file_records.append({
                    "path": rel,
                    "size": size,
                    "sha256": sha,
                    "classification": classification,
                    "machine": machine_hex,
                    "machine_name": pe.get("machine_name"),
                    "is_native": is_native,
                    "is_r2r": is_r2r,
                    "valid": True
                })
            else:
                other_files_count += 1
                cls = classify_non_pe_file(rel)
                file_records.append({
                    "path": rel,
                    "size": size,
                    "sha256": sha,
                    "classification": cls,
                    "machine": None,
                    "machine_name": None,
                    "is_native": False,
                    "is_r2r": False,
                    "valid": True
                })

    if mismatches:
        print(f"ERROR: Found {len(mismatches)} validation failures in {pkg_dir}!", file=sys.stderr)
        for m in mismatches:
            print(f"  {m['file']} ({m['type']}): {m}", file=sys.stderr)
        raise RuntimeError("Package audit check failed!")

    total_managed = il_anycpu_count + managed_target_count
    total_files = len(file_records)
    print(f"Audit Summary for {pkg_dir}:")
    print(f"  Total Packaged Files: {total_files}")
    print(f"  Native PE Binaries (Machine {expected_machine_hex}): {native_count}")
    print(f"  Managed Neutral IL (AnyCPU, Machine 0x014c): {il_anycpu_count}")
    print(f"  Managed Target-Specific (Machine {expected_machine_hex}): {managed_target_count} (ReadyToRun: {r2r_count})")
    print(f"  Total Managed Assemblies: {total_managed}")
    print(f"  Other Package Files: {other_files_count}")
    print(f"  Validation Mismatches: 0")

    return {
        "total_files": total_files,
        "native_count": native_count,
        "il_anycpu_count": il_anycpu_count,
        "managed_target_count": managed_target_count,
        "r2r_count": r2r_count,
        "managed_total_count": total_managed,
        "other_files_count": other_files_count,
        "mismatches": 0,
        "files": file_records
    }


def create_zip(rid):
    src_dir = os.path.join(ARTIFACTS_DIR, rid)
    zip_name = f"aptv-windows-{rid}.zip"
    zip_path = os.path.join(ARTIFACTS_DIR, zip_name)

    print(f"Creating candidate archive: {zip_path}...")
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for root, _, files in sorted(os.walk(src_dir)):
            for f in sorted(files):
                full_path = os.path.join(root, f)
                rel_path = os.path.join(rid, os.path.relpath(full_path, src_dir))
                zf.write(full_path, rel_path)

    sha = sha256_file(zip_path)
    size = os.path.getsize(zip_path)
    print(f"Created {zip_name}: {size:,} bytes, SHA256: {sha}")
    return {
        "filename": zip_name,
        "path": zip_path,
        "size_bytes": size,
        "sha256": sha
    }


def main():
    os.makedirs(ARTIFACTS_DIR, exist_ok=True)
    commit = get_git_commit()

    print("=== Starting APTV Windows Release Packaging ===")
    print(f"Current Git Commit: {commit}")

    x64_audit = publish_rid("win-x64", "0x8664")
    x64_zip = create_zip("win-x64")

    arm64_audit = publish_rid("win-arm64", "0xaa64")
    arm64_zip = create_zip("win-arm64")

    # Entry executable hashes
    x64_exe = os.path.join(ARTIFACTS_DIR, "win-x64", "Iptv.Desktop.exe")
    arm64_exe = os.path.join(ARTIFACTS_DIR, "win-arm64", "Iptv.Desktop.exe")

    entries = {
        "win-x64": {
            "exe_path": "win-x64/Iptv.Desktop.exe",
            "exe_size": os.path.getsize(x64_exe),
            "exe_sha256": sha256_file(x64_exe),
            "zip": x64_zip,
            "total_files_count": x64_audit["total_files"],
            "native_binaries_count": x64_audit["native_count"],
            "managed_il_anycpu_count": x64_audit["il_anycpu_count"],
            "managed_target_specific_count": x64_audit["managed_target_count"],
            "ready_to_run_count": x64_audit["r2r_count"],
            "managed_total_count": x64_audit["managed_total_count"],
            "other_files_count": x64_audit["other_files_count"]
        },
        "win-arm64": {
            "exe_path": "win-arm64/Iptv.Desktop.exe",
            "exe_size": os.path.getsize(arm64_exe),
            "exe_sha256": sha256_file(arm64_exe),
            "zip": arm64_zip,
            "total_files_count": arm64_audit["total_files"],
            "native_binaries_count": arm64_audit["native_count"],
            "managed_il_anycpu_count": arm64_audit["il_anycpu_count"],
            "managed_target_specific_count": arm64_audit["managed_target_count"],
            "ready_to_run_count": arm64_audit["r2r_count"],
            "managed_total_count": arm64_audit["managed_total_count"],
            "other_files_count": arm64_audit["other_files_count"]
        }
    }

    manifest = {
        "version": "1.0.0",
        "product_name": "IPTV Desktop (Windows)",
        "build_provenance": {
            "code_snapshot_commit": get_code_snapshot_commit(),
            "package_metadata_commit": commit,
            "dotnet_sdk_version": "10.0.401"
        },
        "distribution_type": "Pre-release testing candidate (Self-contained, portable ZIP)",
        "disclaimer": {
            "platform_verification": "Built and statically verified on macOS Darwin ARM64. Physical Windows hardware runtime unverified.",
            "hardware_not_verified": True,
            "license_source_offer": "Upstream source repositories cited. Full corresponding source code bundle designated as Pending Plan for official release.",
            "proprietary_assets": "No macOS APTV proprietary icons, trademarks, or cloud services included."
        },
        "artifacts": entries,
        "files": {
            "win-x64": x64_audit["files"],
            "win-arm64": arm64_audit["files"]
        }
    }

    manifest_path = os.path.join(ARTIFACTS_DIR, "manifest.json")
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)

    checksums_path = os.path.join(ARTIFACTS_DIR, "checksums.txt")
    with open(checksums_path, "w", encoding="utf-8") as f:
        f.write(f"{x64_zip['sha256']}  {x64_zip['filename']}\n")
        f.write(f"{arm64_zip['sha256']}  {arm64_zip['filename']}\n")
        f.write(f"{entries['win-x64']['exe_sha256']}  win-x64/Iptv.Desktop.exe\n")
        f.write(f"{entries['win-arm64']['exe_sha256']}  win-arm64/Iptv.Desktop.exe\n")

    print("\n=== Release Packaging Completed Successfully ===")
    print(f"Manifest: {manifest_path}")
    print(f"Checksums: {checksums_path}")


if __name__ == "__main__":
    main()
