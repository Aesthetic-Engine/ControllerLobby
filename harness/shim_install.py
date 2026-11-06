"""Launcher shim configuration transaction (LAUNCHER-SHIM.md section 6; slice S2).

Usage:
  python -I shim_install.py install --receipt <shim build receipt> --record <new record file> --approved <token> --root <scratch-or-selected-LaunchBox-root>
  python -I shim_install.py preview --receipt <...> --record <record it would create> --root <...>   (read-only)
  python -I shim_install.py verify  --receipt <...> --record <record> --root <...>
  python -I shim_install.py remove  --receipt <...> --record <record> --root <...>

Install refuses unless BigBox and LaunchBox are known to be closed, both manifests match their pins, every source file
matches its manifest line, no destination exists, and both emulator entries hold the recorded original ApplicationPath.

Files (creation ownership): each destination is journaled PENDING with a random nonce, written to a stage
file named by that nonce, journaled STAGED, then moved into place with a no-replace rename, then journaled DONE with its
hash. A destination belongs to this install only if it was STAGED, its stage file is gone, and no FOREIGN line says the
rename found the destination taken. Anything else at a destination is kept and reported.

Emulators.xml (single writer): each compare-and-swap reads and writes the file in place through one handle
opened with share mode 0, so no other process can read, write, delete or replace it during the read-compare-write; a
concurrent writer gets a sharing violation instead of a silently lost edit. The pre-image is copied beside the record and
journaled (XML WRITE pre post) before the write and XML WRITTEN after; a write left unfinished whose file is neither the
pre- nor the post-image is reported torn and nothing is touched. Only the ApplicationPath text of the entry with the
pinned ID changes; the rest of the file is byte for byte unchanged. The whole file is never restored from a backup.

Remove validates the whole record against the plan, restores both entries by compare-and-swap, then deletes nothing
unless both planned entries re-reads as their originals, and deletes only files that belong to this install with the
planned hash. Every conflict or uncertainty keeps files and record and reports RECOVERY INCOMPLETE (exit 5).
"""
import ctypes, difflib, hashlib, json, msvcrt, os, pathlib, re, subprocess, sys, uuid
from ctypes import wintypes

# Pins: clean ed9d9c6 terminal-close candidate, 2026-11-03-close-fault-candidate-build-01.
# Esc-only diagnostic trial; no real forwarding. Transaction and recovery guards unchanged.
# Synthetic Schema 1 fixture defaults; Schema 2 configure() replaces these.
SHIM_MANIFEST_PIN = "0000000000000000000000000000000000000000000000000000000000000000"
LOBBY_MANIFEST_PIN = "1111111111111111111111111111111111111111111111111111111111111111"
ENTRIES = [  # (emulator ID, emulator folder relative to the LaunchBox root, original executable)
    ("11111111-1111-4111-8111-111111111111", "Emulators\\RetroArch", "retroarch.exe"),
    ("22222222-2222-4222-8222-222222222222", "Emulators\\MAME 0.279", "mame.exe"),
]
SHIM_EXE = "ControllerLobby.Shim.exe"
pre_redirect_check = None
ALLOWED_GEN_PATHS = None
# Offline checks only. Unset in real use.
TEST_FAIL_AT = os.environ.get("SHIM_INSTALL_TEST_FAIL_AT", "")  # "<step>" raises; "kill:<step>" ends the process
TEST_FRONTEND = os.environ.get("SHIM_INSTALL_TEST_FRONTEND", "")  # "closed"/"running" skips the process query
TEST_TASKLIST_CMD = os.environ.get("SHIM_INSTALL_TEST_TASKLIST_CMD", "")  # JSON command replacing tasklist
TEST_INJECT = os.environ.get("SHIM_INSTALL_TEST_INJECT", "")  # "foreign-after-pending" or "xml-race"
TEST_RACE_SCRIPT = os.environ.get("SHIM_INSTALL_TEST_RACE_SCRIPT", "")

SHARE_NONE = 0  # guard:single-writer
_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_k32.CreateFileW.restype = wintypes.HANDLE
_k32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
INVALID_HANDLE = ctypes.c_void_p(-1).value


class Refused(Exception):
    pass


_locks_opened = 0


def check_no_reparse_under_root(root, target_path):
    """Verify root and every path segment from root to target_path is free of symlinks, junctions and escapes."""
    root_p = pathlib.Path(root)
    if root_p.exists():
        if root_p.is_symlink() or (getattr(os.lstat(root_p), "st_file_attributes", 0) & 0x400):
            raise Refused("Reparse configuration path: " + str(root_p))
    root_resolved = root_p.resolve()
    target_p = pathlib.Path(target_path)
    chain = []
    cur = target_p
    while True:
        chain.append(cur)
        if cur == root_p or cur.parent == cur:
            break
        cur = cur.parent
    for p in reversed(chain):
        if p.exists() or p.is_symlink():
            st = os.lstat(p)
            if p.is_symlink() or (getattr(st, "st_file_attributes", 0) & 0x400):  # guard:reparse-xml
                raise Refused("Reparse configuration path: " + str(p))
    if not target_p.resolve().is_relative_to(root_resolved):
        raise Refused("Configuration path escapes root: " + str(target_p))
    return str(target_p)


def xml_path_for_root(root):
    return check_no_reparse_under_root(root, os.path.join(root, "Data", "Emulators.xml"))


def test_owner_edit(xml_path, step):
    """Offline checks only: at the named step, change another emulator's Title the way an owner edit would."""
    if TEST_INJECT != "owner-edit:" + step:
        return
    raw = open(xml_path, "rb").read().decode("utf-8")
    m = [m for m in re.finditer(r"<Emulator>(?=\s*<).*?</Emulator>", raw, re.S)
         if not any("<ID>" + e[0] + "</ID>" in m.group(0) for e in ENTRIES)][0]
    block = re.sub(r"<Title>.*?</Title>", lambda _: "<Title>Owner Edit " + step + "</Title>", m.group(0), count=1, flags=re.S)
    open(xml_path, "wb").write((raw[:m.start()] + block + raw[m.end():]).encode("utf-8"))


def sha_bytes(b):
    return hashlib.sha256(b).hexdigest().upper()


def sha(path):
    return sha_bytes(open(path, "rb").read())


def sidecar_bytes(target):
    return (json.dumps({"target": target, "mode": "esc-only"}) + "\n").encode("utf-8")


def source_bytes(src):
    if isinstance(src, tuple):
        return src[1] if src[0] == "bytes" else sidecar_bytes(src[1])
    return open(src, "rb").read()


def stage_path(dst, nonce):
    return dst + ".cl-" + nonce + ".part"


def maybe_fail(step):
    if TEST_FAIL_AT == step:
        raise RuntimeError("test: injected failure at " + step)
    if TEST_FAIL_AT == "kill:" + step:
        os._exit(9)


def frontend_running():
    """True or False only when the process listing is known good; None when it cannot be told ."""
    if TEST_FRONTEND:
        return TEST_FRONTEND == "running"
    cmd = json.loads(TEST_TASKLIST_CMD) if TEST_TASKLIST_CMD else ["tasklist", "/FO", "CSV", "/NH"]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
    except (OSError, subprocess.SubprocessError):
        return None
    out = (r.stdout or "").lower()
    me = '"' + os.path.basename(sys.executable).lower() + '"'
    if r.returncode != 0 or me not in out:  # guard:frontend-known: a listing that does not show this process is not trusted
        return None
    return '"bigbox.exe"' in out or '"launchbox.exe"' in out


def require_frontend_closed(what):
    state = frontend_running()
    if state is None:
        raise Refused("cannot tell whether BigBox or LaunchBox is running; not " + what)
    if state:
        raise Refused("BigBox or LaunchBox is running; not " + what)


def manifest(path, pin, name):
    if not os.path.isfile(path):
        raise Refused("source missing: " + name)
    if sha(path) != pin:
        raise Refused("pin mismatch: " + name)
    lines = {}
    for line in open(path, encoding="utf-8").read().splitlines():
        if not line:
            continue
        m = re.fullmatch(r"([0-9A-F]{64})  ([A-Za-z0-9._ -]+)", line)
        if not m or m.group(2) in lines:
            raise Refused("malformed manifest line in " + name + ": " + line)
        lines[m.group(2)] = m.group(1)
    return lines


def plan(receipt, root):
    """Every destination with its source and expected hash, per entry, plus the two XML edits."""
    shim = manifest(os.path.join(receipt, "shim.sha256"), SHIM_MANIFEST_PIN, "shim.sha256")
    lobby = manifest(os.path.join(receipt, "lobby.sha256"), LOBBY_MANIFEST_PIN, "lobby.sha256")
    files, edits = [], []
    for eid, folder, target in ENTRIES:
        base = os.path.join(root, folder)
        for name, h in shim.items():
            files.append((os.path.join(receipt, "shim", name), os.path.join(base, name), h))
        for name, h in lobby.items():
            files.append((os.path.join(receipt, "lobby", name), os.path.join(base, "ControllerLobby", "lobby", name), h))
        files.append((os.path.join(receipt, "lobby.sha256"), os.path.join(base, "ControllerLobby", "lobby.sha256"), LOBBY_MANIFEST_PIN))
        files.append((("sidecar", target), os.path.join(base, "ControllerLobby.Shim.json"), sha_bytes(sidecar_bytes(target))))
        edits.append((eid, folder + "\\" + target, folder + "\\" + SHIM_EXE))
    return files, edits


# ---- Emulators.xml
def find_entry(text, eid):
    spans = [m for m in re.finditer(r"<Emulator>(?=\s*<).*?</Emulator>", text, re.S) if "<ID>" + eid + "</ID>" in m.group(0)]
    if len(spans) != 1:
        raise Refused("emulator entry not found exactly once: " + eid)
    block = spans[0]
    paths = list(re.finditer(r"<ApplicationPath>(.*?)</ApplicationPath>", block.group(0), re.S))
    if len(paths) != 1:
        raise Refused("ApplicationPath not found exactly once in entry " + eid)
    p = paths[0]
    return block.start() + p.start(1), block.start() + p.end(1), p.group(1)


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def open_exclusive(path):
    global _locks_opened
    _locks_opened += 1
    if TEST_INJECT == "deny-lock-%d" % _locks_opened:
        raise Refused("test: Emulators.xml is in use by another process; not changed")
    st = os.lstat(path)
    if getattr(st, "st_file_attributes", 0) & 0x400:
        raise Refused("Reparse configuration path: " + path)
    h = _k32.CreateFileW(path, 0x80000000 | 0x40000000, SHARE_NONE, None, 3, 0x80, None)  # read|write, OPEN_EXISTING
    if h is None or h == INVALID_HANDLE:
        raise Refused("Emulators.xml is in use by another process (Windows error %d); not changed" % ctypes.get_last_error())
    return os.fdopen(msvcrt.open_osfhandle(h, 0), "r+b")


def maybe_inject_race(xml_path):
    if TEST_INJECT == "xml-race":
        subprocess.run([sys.executable, "-I", TEST_RACE_SCRIPT, xml_path], timeout=30)


def cas(xml_path, eid, expected, new, record, image=None):
    """Compare-and-swap one entry's ApplicationPath under an exclusive handle. With image, the whole file must be exactly
    that image, checked under the same lock . Returns (None, post-image hash) or (conflict reason, None)."""
    with open_exclusive(xml_path) as f:
        raw = f.read()
        if image is not None and sha_bytes(raw) != image:  # guard:locked-image
            return "conflict: Emulators.xml changed since the approved preview", None
        text = raw.decode("utf-8")
        start, end, current = find_entry(text, eid)
        if current != xml_escape(expected):  # guard:cas
            return "conflict: " + eid + " is " + current, None
        out = (text[:start] + xml_escape(new) + text[end:]).encode("utf-8")
        maybe_inject_race(xml_path)
        pre = record + ".preimage-" + sha_bytes(raw)[:16] + ".xml"
        if not os.path.exists(pre):
            with open(pre, "wb") as p:
                p.write(raw)
        append(record, "XML WRITE " + sha_bytes(raw) + " " + sha_bytes(out) + " *" + pre)
        f.seek(0)
        if TEST_FAIL_AT == "kill:mid-xml-write":
            f.write(out[:len(text[:start].encode("utf-8")) + len(xml_escape(new).encode("utf-8")) + 1]); f.flush(); os._exit(9)  # cut just past the edit
        f.write(out)
        f.truncate()
        f.flush()
        os.fsync(f.fileno())
        f.seek(0)
        if f.read() != out:
            return "verify failed: " + eid, None
    append(record, "XML WRITTEN " + sha_bytes(out))
    return None, sha_bytes(out)


# ---- Record
def append(record, line):
    with open(record, "a", encoding="utf-8") as f:
        f.write(line + "\n")
        f.flush()
        os.fsync(f.fileno())


def preflight(receipt, record, root):
    """Every install refusal except the frontend state. Reads only."""
    if os.path.exists(record):
        raise Refused("install record exists; never overwrite")
    xml_path = xml_path_for_root(root)
    files, edits = plan(receipt, root)
    raw = open(xml_path, "rb").read()  # the one snapshot validation, display, token and the first swap all use
    text = raw.decode("utf-8")
    for eid, original, _ in edits:
        if find_entry(text, eid)[2] != xml_escape(original):  # guard:preflight-original
            raise Refused("entry " + eid + " is not the recorded original; nothing changed")
    for src, dst, h in files:
        if os.path.exists(dst):
            raise Refused("destination exists: " + dst)
        if isinstance(src, tuple):
            if sha_bytes(source_bytes(src)) != h:
                raise Refused("pin mismatch: " + dst)
        else:
            if not os.path.isfile(src):
                raise Refused("source missing: " + src)
            if sha(src) != h:
                raise Refused("pin mismatch: " + src)
    return files, edits, xml_path, raw


def approval_token(receipt, record, files, edits, raw):
    """SHA-256 of the canonical binding the user approves: Emulators.xml bytes, both entries, every destination with
    its planned hash, both manifest pins, receipt and record paths ."""
    binding = {"emulators_xml_sha256": sha_bytes(raw), "edits": [list(e) for e in edits],
               "files": [[d, h] for _, d, h in files], "pins": [SHIM_MANIFEST_PIN, LOBBY_MANIFEST_PIN],
               "receipt": os.path.abspath(receipt), "record": os.path.abspath(record)}
    return sha_bytes(json.dumps(binding, sort_keys=True, separators=(",", ":")).encode("utf-8"))


def preview(receipt, record, root):
    """Read-only: the exact Emulators.xml diff and the deployment install would make now. Writes nothing (S3)."""
    files, edits, xml_path, raw = preflight(receipt, record, root)
    test_owner_edit(xml_path, "after-preview-snapshot")
    text = raw.decode("utf-8")
    after = text
    for eid, original, new in edits:
        start, end, _ = find_entry(after, eid)
        after = after[:start] + xml_escape(new) + after[end:]
    state = frontend_running()
    lines = ["Emulators.xml " + xml_path + " sha256 " + sha_bytes(raw),
             "Entries (ID, title):"]
    for eid, original, new in edits:
        block = [m.group(0) for m in re.finditer(r"<Emulator>(?=\s*<).*?</Emulator>", text, re.S) if "<ID>" + eid + "</ID>" in m.group(0)][0]
        title = re.search(r"<Title>(.*?)</Title>", block, re.S)
        lines.append("  " + eid + " " + (title.group(1) if title else "?"))
    lines.append("Exact diff (no context lines; every other byte unchanged):")
    lines += ["  " + l.rstrip("\r\n") for l in difflib.unified_diff(text.splitlines(True), after.splitlines(True), "before", "after", n=0)]
    lines.append("Files: %d new files, none existing (shim manifest %s, lobby manifest %s)" % (len(files), SHIM_MANIFEST_PIN[:16], LOBBY_MANIFEST_PIN[:16]))
    for folder in sorted({os.path.dirname(d) for _, d, _ in files}):
        lines.append("  " + folder + ": " + str(sum(1 for _, d, _ in files if os.path.dirname(d) == folder)))
    lines.append("Install record (new): " + record)
    lines.append("BigBox/LaunchBox now: " + {True: "RUNNING", False: "closed", None: "UNKNOWN"}[state] + " (install requires closed)")
    lines.append("APPROVAL TOKEN: " + approval_token(receipt, record, files, edits, raw))  # guard:snapshot-token
    lines.append("PREVIEW ONLY: nothing was changed")
    return "\n".join(lines)


def install(receipt, record, root, approved):
    require_frontend_closed("installing")
    files, edits, xml_path, raw = preflight(receipt, record, root)
    if not approved:
        raise Refused("no approval token; run preview and approve its exact output first")
    if approval_token(receipt, record, files, edits, raw) != approved:  # guard:approved-token
        raise Refused("approval is stale: the build, the entries or Emulators.xml differ from the approved preview; nothing changed")
    image = sha_bytes(raw)  # the approved image; each swap must find exactly the image it expects, under its lock
    test_owner_edit(xml_path, "after-token")
    os.makedirs(os.path.dirname(os.path.abspath(record)), exist_ok=True)
    append(record, "# shim install record, receipt " + receipt + ", approved Emulators.xml " + image)
    done = 0
    try:
        for src, dst, h in files:
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            nonce = uuid.uuid4().hex
            append(record, "FILE PENDING " + nonce + " *" + dst)  # journal before any write
            if done == 2:
                maybe_fail("after-pending")
                if TEST_INJECT == "foreign-after-pending":  # another process creates the destination, with the planned bytes
                    with open(dst, "wb") as foreign:
                        foreign.write(source_bytes(src))
            stage = stage_path(dst, nonce)
            with open(stage, "xb") as f:
                f.write(source_bytes(src))
                f.flush()
                os.fsync(f.fileno())
            if done == 2:
                maybe_fail("after-create")
            if sha(stage) != h:  # the stage is complete and verified before it is journaled STAGED
                raise RuntimeError("stage hash mismatch: " + stage)
            append(record, "FILE STAGED " + nonce + " *" + dst)
            if done == 2:
                maybe_fail("after-staged")
            try:
                os.rename(stage, dst)  # never replaces an existing destination on Windows
            except FileExistsError:
                append(record, "FILE FOREIGN " + nonce + " *" + dst)
                os.remove(stage)
                raise RuntimeError("destination appeared during install, kept: " + dst)
            if done == 2:
                maybe_fail("after-rename")
            got = sha(dst)
            append(record, "FILE DONE " + got + " *" + dst)
            if got != h:
                raise RuntimeError("copy hash mismatch: " + dst)
            done += 1
            if done == 3:
                maybe_fail("after-files-partial")
        maybe_fail("after-files")
        if pre_redirect_check is not None:  # guard:pre-redirect-check
            pre_redirect_check(receipt, record, root)
        require_frontend_closed("changing Emulators.xml")
        for eid, original, new in edits:
            append(record, "XML PENDING " + eid + " *" + original + " -> " + new)
            maybe_fail("after-xml-pending")
            why, image = cas(xml_path, eid, original, new, record, image)
            if why:
                raise RuntimeError(why)
            test_owner_edit(xml_path, "between-swaps")
            append(record, "XML DONE " + eid + " *" + original + " -> " + new)
            maybe_fail("after-first-xml")
    except Exception as e:
        append(record, "# FAILED: " + str(e) + "; rolling back")
        try:
            report = remove(receipt, record, root, rollback=True)
        except Refused as r:
            report = "RECOVERY INCOMPLETE: " + str(r)
        append(record, "# rolled back: " + report)
        raise RuntimeError("install failed (" + str(e) + "); " + report)
    append(record, "# COMPLETE %d files, %d entries" % (done, len(edits)))
    return "installed %d files, %d entries" % (done, len(edits))


def read_record(receipt, record, root):
    """The whole record, validated against the plan and its own state order, before remove acts on any of it."""
    files, edits = plan(receipt, root)
    by_dst = {os.path.normcase(os.path.abspath(d)): (s, h) for s, d, h in files}
    known_edits = {(e, o, n) for e, o, n in edits}
    if not os.path.isfile(record):
        raise Refused("no install record: " + record)
    entries, state, xml, open_write = [], {}, {}, None
    gens_seen = {}
    for line in open(record, encoding="utf-8").read().splitlines():
        if not line or line.startswith("#"):
            continue
        m = re.fullmatch(r"FILE (PENDING|STAGED|FOREIGN) ([0-9a-f]{32}) \*(.+)", line) or re.fullmatch(r"FILE (DONE) ([0-9A-F]{64}) \*(.+)", line)
        if m:
            kind, val, path = m.groups()
            k = os.path.normcase(os.path.abspath(path))
            if k not in by_dst:
                raise Refused("record path outside the plan: " + path)
            e = state.get(k)
            if kind == "PENDING":
                if e:
                    raise Refused("duplicate record path: " + path)
                state[k] = e = {"path": path, "nonce": val, "staged": False, "foreign": False, "done": None,
                                "hash": by_dst[k][1], "allowed_hashes": {by_dst[k][1]}, "extra_nonces": [],
                                "extra_stages": {}, "open_gen": None, "last_gen": 1, "last_gen_info": None}
                entries.append(e)
            elif not e or (kind != "DONE" and e["nonce"] != val):
                raise Refused("record line without its PENDING line: " + line)
            elif kind == "STAGED":
                if e["staged"]:
                    raise Refused("record state out of order: " + line)
                e["staged"] = True
            elif kind == "FOREIGN":
                if not e["staged"] or e["done"]:
                    raise Refused("record state out of order: " + line)
                e["foreign"] = True
            else:
                if not e["staged"] or e["foreign"] or e["done"]:
                    raise Refused("record state out of order: " + line)
                e["allowed_hashes"].add(val)
                e["done"] = val
            continue
        m = (
            re.fullmatch(r"GEN (PENDING|STAGED) (\d+) ([0-9a-f]{32}) ([0-9A-F]{64}) ([0-9A-F]{64}) \*(.+)", line)
            or re.fullmatch(r"GEN (ABANDONED) (\d+) ([0-9a-f]{32}) ([0-9A-F]{64}) \*(.+)", line)
            or re.fullmatch(r"GEN (DONE) (\d+) ([0-9A-F]{64}) \*(.+)", line)
        )
        if m:
            groups = m.groups()
            kind = groups[0]
            gen = int(groups[1])
            path = groups[-1]
            k = os.path.normcase(os.path.abspath(path))
            e = state.get(k)
            if not e or not e["done"]:
                raise Refused("generation line without completed base file: " + line)
            if ALLOWED_GEN_PATHS is not None and k not in ALLOWED_GEN_PATHS:
                raise Refused("generation line for non-generated file: " + line)
            if gen < 2:
                raise Refused("generation state out of order: " + line)
            for prev_g, prev_map in gens_seen.items():
                if prev_g < gen:
                    all_ab = all(st == "ABANDONED" for st in prev_map.values())
                    all_dn = (ALLOWED_GEN_PATHS is None or set(prev_map.keys()) == set(ALLOWED_GEN_PATHS)) and all(st == "DONE" for st in prev_map.values())
                    if not (all_ab or all_dn):
                        raise Refused("generation state out of order: " + line)
            if kind == "PENDING":
                nonce, old_sha, new_sha = groups[2], groups[3], groups[4]
                g_cur = gens_seen.get(gen, {})
                if g_cur and all(st == "ABANDONED" for st in g_cur.values()):
                    gens_seen[gen] = g_cur = {}
                if e["open_gen"] is not None or old_sha != e["done"] or gen <= e["last_gen"] or k in g_cur:
                    raise Refused("generation state out of order: " + line)
                e["extra_nonces"].append(nonce)
                e["extra_stages"][nonce] = new_sha
                e["open_gen"] = {"gen": gen, "nonce": nonce, "old_sha": old_sha, "new_sha": new_sha, "staged": False}
                gens_seen.setdefault(gen, {})[k] = "PENDING"
            elif kind == "STAGED":
                nonce, old_sha, new_sha = groups[2], groups[3], groups[4]
                og = e["open_gen"]
                if not og or (og["gen"], og["nonce"], og["old_sha"], og["new_sha"], og["staged"]) != (gen, nonce, old_sha, new_sha, False):
                    raise Refused("generation state out of order: " + line)
                og["staged"] = True
                e["allowed_hashes"].add(new_sha)
                gens_seen.setdefault(gen, {})[k] = "STAGED"
            elif kind == "ABANDONED":
                nonce, old_sha = groups[2], groups[3]
                og = e["open_gen"]
                lgi = e.get("last_gen_info")
                g_cur = gens_seen.get(gen, {})
                already_fully_done = (ALLOWED_GEN_PATHS is not None and set(g_cur.keys()) == set(ALLOWED_GEN_PATHS) and all(st == "DONE" for st in g_cur.values()))
                valid_open = og is not None and (og["gen"], og["nonce"], og["old_sha"]) == (gen, nonce, old_sha)
                valid_partial_done = og is None and lgi is not None and (lgi["gen"], lgi["nonce"], lgi["old_sha"]) == (gen, nonce, old_sha) and not already_fully_done
                if not (valid_open or valid_partial_done):
                    raise Refused("generation state out of order: " + line)
                e["done"] = old_sha
                e["open_gen"] = None
                e["last_gen_info"] = None
                e["last_gen"] = gen - 1
                gens_seen.setdefault(gen, {})[k] = "ABANDONED"
            else:
                new_sha = groups[2]
                og = e["open_gen"]
                if not og or og["gen"] != gen or og["new_sha"] != new_sha or not og["staged"]:
                    raise Refused("generation state out of order: " + line)
                e["allowed_hashes"].add(new_sha)
                e["done"] = new_sha
                e["last_gen"] = gen
                e["last_gen_info"] = dict(og)
                e["open_gen"] = None
                gens_seen.setdefault(gen, {})[k] = "DONE"
            continue
        m = re.fullmatch(r"XML (PENDING|DONE) (\S+) \*(.+) -> (.+)", line)
        if m:
            key = (m.group(2), m.group(3), m.group(4))
            if key not in known_edits:
                raise Refused("record edit outside the plan: " + line)
            if m.group(1) == "DONE" and m.group(2) not in xml:
                raise Refused("record state out of order: " + line)
            xml[m.group(2)] = (m.group(1), m.group(3), m.group(4))
            continue
        m = re.fullmatch(r"XML WRITE ([0-9A-F]{64}) ([0-9A-F]{64}) \*(.+)", line)
        if m:
            open_write = m.groups()
            continue
        m = re.fullmatch(r"XML (?:WRITTEN|ABANDONED) ([0-9A-F]{64})", line)
        if m:
            if not open_write or open_write[1] != m.group(1):
                raise Refused("record state out of order: " + line)
            open_write = None
            continue
        raise Refused("malformed record line: " + line)
    any_open_gen = any(e["open_gen"] is not None for e in entries)
    if not any_open_gen and gens_seen:
        max_g = max(gens_seen)
        g_map = gens_seen[max_g]
        if not all(st == "ABANDONED" for st in g_map.values()):
            if ALLOWED_GEN_PATHS is not None and (set(g_map.keys()) != set(ALLOWED_GEN_PATHS) or any(st != "DONE" for st in g_map.values())):
                any_open_gen = True
    for e in entries:
        if e["done"] is not None:
            ek = os.path.normcase(os.path.abspath(e["path"]))
            if any_open_gen and (ALLOWED_GEN_PATHS is None or ek in ALLOWED_GEN_PATHS):
                if e["hash"] not in e["allowed_hashes"]:
                    raise Refused("record hash is not the planned hash: " + e["path"])
            elif e["done"] != e["hash"]:  # guard:record-hash-plan
                raise Refused("record hash is not the planned hash: " + e["path"])
    return entries, xml, open_write


def incomplete(record, why):
    msg = "RECOVERY INCOMPLETE: " + why + "; files and record kept"
    append(record, "# " + msg)
    return msg


def remove(receipt, record, root, rollback=False):
    if not rollback:
        require_frontend_closed("removing")
    xml_path = xml_path_for_root(root)
    entries, xml, open_write = read_record(receipt, record, root)
    if open_write and sha(xml_path) not in (open_write[0], open_write[1]):  # guard:torn
        return incomplete(record, "Emulators.xml was being written when the run ended and is neither the pre- nor the post-image; configuration NOT restored (pre-image copy: " + open_write[2] + ")")
    if open_write:
        # The run ended inside a write but the file is whole: close that write as done or as never applied.
        append(record, ("XML WRITTEN " if sha(xml_path) == open_write[1] else "XML ABANDONED ") + open_write[1])
    # 1. Restore every recorded entry (compare-and-swap back).
    conflicts = []
    for eid, (status, original, new) in xml.items():
        try:
            current = find_entry(open(xml_path, encoding="utf-8").read(), eid)[2]
        except Refused as r:
            return incomplete(record, str(r))
        if current == xml_escape(original):
            continue
        try:
            why, _ = cas(xml_path, eid, new, original, record)
        except Refused as r:  # guard:partial-restore: a refusal here may follow an entry already restored
            why = str(r)
        if why:
            conflicts.append(why)
        else:
            maybe_fail("after-first-restore")
    if conflicts:  # guard:restore-before-delete
        return incomplete(record, "; ".join(conflicts))
    # 2. Nothing is deleted unless every planned entry re-reads as its original .
    try:
        text = open(xml_path, encoding="utf-8").read()
        unsafe = [eid for eid, folder, target in ENTRIES if find_entry(text, eid)[2] != xml_escape(folder + "\\" + target)]
    except Refused as r:
        return incomplete(record, str(r))
    if unsafe:  # guard:entries-safe
        return incomplete(record, "entries not at their originals: " + ", ".join(unsafe))
    # 3. Delete only what this install created, with the planned bytes.
    kept = []
    for e in entries:
        dst, stage = e["path"], stage_path(e["path"], e["nonce"])
        stage_exists = os.path.exists(stage)
        ours = e["staged"] and not stage_exists and not e["foreign"]  # guard:ownership
        if os.path.exists(dst):
            if ours and sha(dst) in e.get("allowed_hashes", {e["hash"]}):
                os.remove(dst)
            else:
                kept.append(dst)
        if stage_exists:
            os.remove(stage)  # the stage file's random name was created exclusively by this install
        for n in e.get("extra_nonces", []):
            es = stage_path(e["path"], n)
            if os.path.exists(es):
                if not os.path.islink(es) and sha(es) == e.get("extra_stages", {}).get(n):
                    os.remove(es)
                else:
                    kept.append(es)
    for _, folder, _ in ENTRIES:
        for sub in ("ControllerLobby\\lobby", "ControllerLobby", "ControllerLobby.BigBox"):
            d = os.path.join(root, folder, sub)
            if os.path.isdir(d) and not os.listdir(d):
                os.rmdir(d)
    if kept:
        return incomplete(record, "files not created by this install or changed since, kept: " + "; ".join(kept))
    append(record, "# REMOVED")
    return "removed"


def verify(receipt, record, root):
    """Verified only for the complete planned deployment, whatever the record omits ."""
    xml_path = xml_path_for_root(root)
    files, edits = plan(receipt, root)
    entries, xml, open_write = read_record(receipt, record, root)
    got = {os.path.normcase(os.path.abspath(e["path"])): e for e in entries}
    bad = []
    for _, dst, h in files:  # guard:verify-full-plan
        e = got.get(os.path.normcase(os.path.abspath(dst)))
        if (not e or e["foreign"] or e["done"] != h or not os.path.exists(dst) or sha(dst) != h
                or e.get("open_gen") is not None
                or os.path.exists(stage_path(e["path"], e["nonce"]))
                or any(os.path.exists(stage_path(e["path"], n)) for n in e.get("extra_nonces", []))):
            bad.append(dst)
    text = open(xml_path, encoding="utf-8").read()
    for eid, original, new in edits:  # guard:verify-edits
        if xml.get(eid) != ("DONE", original, new) or find_entry(text, eid)[2] != xml_escape(new):
            bad.append(eid)
    if open_write:
        bad.append("unfinished XML write")
    return "verified" if not bad else "MISMATCH " + "; ".join(bad)


def check_removed(receipt, record, root):
    """After remove: every planned destination absent, no stage file left, both entries at their originals. Independent of the record's own claims; reads only."""
    xml_path = xml_path_for_root(root)
    files, edits = plan(receipt, root)
    bad = [d for _, d, _ in files if os.path.exists(d)]  # guard:removed-files
    for folder in sorted({os.path.dirname(d) for _, d, _ in files}):  # guard:stage-scan: record-independent
        try:
            names = os.listdir(folder)
        except FileNotFoundError:
            continue
        except OSError as e:
            raise Refused("cannot establish absence in " + folder + " (" + type(e).__name__ + ")")
        wanted = {os.path.basename(d) for _, d, _ in files if os.path.dirname(d) == folder}
        bad += [os.path.join(folder, n) for n in names
                if any(re.fullmatch(re.escape(w) + r"\.cl-[0-9a-f]{32}\.part", n) for w in wanted)]
    text = open(xml_path, encoding="utf-8").read()
    bad += [eid + " is " + find_entry(text, eid)[2] for eid, original, _ in edits if find_entry(text, eid)[2] != xml_escape(original)]
    return "removed: %d planned files absent, both entries original" % len(files) if not bad else "NOT REMOVED " + "; ".join(bad)


def main():
    args = sys.argv[1:]
    action = args[0]
    opts = dict(zip(args[1::2], args[2::2]))
    try:
        if not opts.get("--root"):
            raise Refused("--root is required")  # guard:explicit-root
        root = opts["--root"]
        if action == "preview":
            print(preview(opts["--receipt"], opts["--record"], root))
        elif action == "install":
            print(install(opts["--receipt"], opts["--record"], root, opts.get("--approved", "")))
        elif action == "remove":
            r = remove(opts["--receipt"], opts["--record"], root)
            print(r)
            sys.exit(0 if not r.startswith("RECOVERY INCOMPLETE") else 5)
        elif action == "check-removed":
            r = check_removed(opts["--receipt"], opts["--record"], root)
            print(r)
            sys.exit(0 if r.startswith("removed:") else 3)
        elif action == "verify":
            r = verify(opts["--receipt"], opts["--record"], root)
            print(r)
            sys.exit(0 if r == "verified" else 3)
    except Refused as e:
        print("REFUSED: " + str(e))
        sys.exit(2)
    except RuntimeError as e:
        print("FAILED: " + str(e))
        sys.exit(4)


if __name__ == "__main__":
    main()
