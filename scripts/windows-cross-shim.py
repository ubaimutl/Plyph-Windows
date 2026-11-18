#!/usr/bin/env python3
import os
import re
import subprocess
import sys


def to_wine_arg(arg: str, is_mt: bool) -> str:
    if arg.startswith("/") and ("/" in arg[1:] or os.path.exists(arg)):
        return "Z:" + arg
    if is_mt and arg.startswith("-out:/"):
        return "-out:Z:" + arg[5:]
    if not is_mt and ":" in arg and arg.split(":", 1)[1].startswith("/"):
        k, v = arg.split(":", 1)
        return f"{k}:Z:{v}"
    return arg


def fix_resfiles(search_dirs: list[str]) -> None:
    seen = set()
    for base in search_dirs:
        if not base or not os.path.isdir(base):
            continue
        for dp, _, fns in os.walk(base):
            for fn in fns:
                if not fn.endswith(".resfiles"):
                    continue
                p = os.path.abspath(os.path.join(dp, fn))
                if p in seen:
                    continue
                seen.add(p)
                with open(p, "r", encoding="utf-8", errors="ignore") as f:
                    orig = f.read()
                fixed = re.sub(r"^/(?=[A-Za-z0-9._-]+/)", "Z:/", orig, flags=re.M)
                if fixed != orig:
                    with open(p, "w", encoding="utf-8") as f:
                        f.write(fixed)


def build_case_map(root_dir: str) -> dict[str, str]:
    case_map: dict[str, str] = {}
    if not os.path.isdir(root_dir):
        return case_map
    for dp, _, fns in os.walk(root_dir):
        for fn in fns:
            full = os.path.join(dp, fn)
            rel = os.path.relpath(full, root_dir).replace(os.sep, "\\")
            case_map[rel.lower()] = rel
    return case_map


def fix_pri_xml(xml_path: str, msix_content_dir: str) -> None:
    if not os.path.isfile(xml_path):
        return
    with open(xml_path, "r", encoding="utf-8", errors="ignore") as f:
        orig = f.read()

    # Trim CRLF + indentation trailing inside <Value>...\r\n\t...</Value> emitted by makepri dump
    fixed = re.sub(r"<Value>([^\r\n<]*?)\s+</Value>", r"<Value>\1</Value>", orig)

    # Fix case differences between PRI metadata and extracted MsixContent files on case-sensitive Linux FS
    case_map = build_case_map(msix_content_dir)
    if case_map:
        def _match_case(m: re.Match[str]) -> str:
            val = m.group(1)
            norm = val.replace("/", "\\").lower()
            actual = case_map.get(norm)
            return f"<Value>{actual}</Value>" if actual else m.group(0)

        fixed = re.sub(r"<Value>([^<\r\n]+\\[^<\r\n]+)</Value>", _match_case, fixed)

    if fixed != orig:
        with open(xml_path, "w", encoding="utf-8") as f:
            f.write(fixed)


def main() -> int:
    if len(sys.argv) < 4:
        sys.stderr.write("Usage: windows-cross-shim.py <wine-run> <tool-exe> <makepri|mt> [args...]\n")
        return 2

    wine_run, tool_exe, mode = sys.argv[1], sys.argv[2], sys.argv[3]
    raw_args = sys.argv[4:]
    is_mt = mode == "mt"

    if not is_mt:
        res_dirs = ["obj"]
        for a in raw_args:
            if a.endswith("priconfig.xml") and os.path.isfile(a):
                res_dirs.append(os.path.dirname(a))
        fix_resfiles(res_dirs)

    mapped_args = [to_wine_arg(a, is_mt) for a in raw_args]
    res = subprocess.run([wine_run, tool_exe] + mapped_args)

    if res.returncode == 0 and not is_mt:
        seen_xml: set[str] = set()
        candidates: list[str] = []
        for i, a in enumerate(raw_args):
            low = a.lower()
            if low in ("-of", "/of") and i + 1 < len(raw_args):
                candidates.append(raw_args[i + 1])
            elif low.startswith(("-of:", "/of:")):
                candidates.append(a.split(":", 1)[1])
        for dp, _, fns in os.walk("obj"):
            for fn in fns:
                if fn.endswith(".pri.xml"):
                    candidates.append(os.path.join(dp, fn))
        for out_file in candidates:
            ap = os.path.abspath(out_file)
            if ap in seen_xml or not out_file.endswith(".xml"):
                continue
            seen_xml.add(ap)
            msix_dir = os.path.join(os.path.dirname(ap), "MsixContent")
            fix_pri_xml(ap, msix_dir)

    return res.returncode


if __name__ == "__main__":
    sys.exit(main())
