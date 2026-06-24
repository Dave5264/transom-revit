#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
transom_verify.py - Compare a Transom-exported .xlsx workbook against the LIVE Revit model.

Single source of truth for "which cells were edited and whether the edit landed after import".

For every writable column and every rendered data row it reports:
    rendered_row (xlsx row), column header, parameterId, binding,
    workbook_cell_value, live_model_value, MATCH / DIFFER.

- TYPE-bound columns  : read the parameter from the TYPE element (row.uniqueId), once.
- INSTANCE-bound cols : read from EACH instance in row.instanceIds; report whether ALL
                        instances equal the workbook cell, and flag if instances disagree.

The tool is self-checking:
  * It reassembles and validates the hidden cowork_meta JSON.
  * It DETECTS the meta.excelRow -> xlsx-row offset empirically (by which offset makes the
    anchor column's uid match meta.uniqueId for the most rows) and FAILS LOUDLY if no
    consistent offset exists.
  * It selects the LIVE document by matching meta.sourceModel.title / .guid among the OPEN
    documents -- it does NOT blindly trust ActiveUIDocument (the active doc may be a
    different project). Override with --doc.

Stdlib only: zipfile, re, json, urllib, argparse.

Usage:
    python transom_verify.py "<workbook.xlsx>" [--all] [--column WIDTH] [--param -1001203]
                                               [--doc "<title substr>"] [--sheet "<name>"]
                                               [--bridge URL] [--show-missing]
"""

import argparse
import json
import re
import socket
import sys
import time
import zipfile
import urllib.request
import urllib.error
from xml.sax.saxutils import unescape as _sax_unescape


class BridgeError(Exception):
    """A bridge call that exhausted retries (timeout/garble/conn). batch_read converts it to a
    LABELED INCOMPLETE result so the run never dies with 0 bytes (T1 fix)."""


_RUN_START = time.time()  # T1: wall-clock budget anchor (TOTAL_BUDGET)


def _budget_exceeded():
    return (time.time() - _RUN_START) > TOTAL_BUDGET

# ROOT-CAUSE FIX (2026-06-20): xml.sax.saxutils.unescape ONLY handles &amp; &lt; &gt; by default — NOT
# &quot; or &apos;. Transom's own xlsx writer stores the meta JSON's double-quotes as LITERAL " inside
# <t> (legal in element text), so single-unescape parsed fine. But when a user OPENS+SAVES in Excel,
# Excel RE-ESCAPES " → &quot; (and ' → &apos;), and the default unescape left those entities in place →
# the reassembled meta JSON was `{&quot;tool&quot;:…}` → json.loads "Expecting value" crash. Passing the
# full entity map makes every cell/shared-string read handle Excel's escaping too. (Used everywhere this
# module unescapes XLSX text.)
_XML_ENTITIES = {"&quot;": '"', "&apos;": "'", "&#34;": '"', "&#39;": "'"}


def unescape(s):
    return _sax_unescape(s, _XML_ENTITIES)

DEFAULT_BRIDGE = "http://localhost:48884/revit_mcp/execute_code/"
ANCHOR_DEFAULT = "__transom_uid__"
BATCH = 5           # element-reads per bridge call (T1 fix: smaller payload → less stall risk; was 10)
RETRIES = 3         # transient-empty / 500 retries per call (was 4)
CALL_TIMEOUT = 45   # T1 fix: per-call socket timeout (s); was 120 — a wedged call now fails fast + LOUD
TOTAL_BUDGET = 600  # T1 fix: hard wall-clock ceiling (s) for the whole live read; on exceed → labeled INCOMPLETE, never a silent 0-byte death
MISSING_RETRIES = 3       # re-read passes for elements that came back not-OK (transient dropout guard)
MISSING_RETRY_SLEEP = 1.5 # seconds to let the doc settle before a missing-retry pass
CHUNK_RETRIES = 2         # re-issue a single batch whose reply is garbled (e.g. stray 'ok') before soft-failing it (was 3)

# Distinct exit codes (T5d): 0 = clean, 1 = real diffs, 2 = INCOMPLETE (couldn't read — timeout/bridgefail)
EXIT_CLEAN, EXIT_DIFF, EXIT_INCOMPLETE = 0, 1, 2


# --------------------------------------------------------------------------- #
#  XLSX parsing (regex-based, matching the working Transom approach)
# --------------------------------------------------------------------------- #
def _read_zip_text(z, name):
    return z.read(name).decode("utf-8", "replace")


def _parse_shared_strings(xml):
    """Return a list of fully-unescaped shared-string values (rich runs concatenated)."""
    out = []
    for si in re.findall(r"<si>(.*?)</si>", xml, re.S):
        parts = re.findall(r"<t[^>]*>(.*?)</t>", si, re.S)
        out.append(unescape("".join(parts)) if parts else "")
    return out


def _col_letters_to_idx(letters):
    n = 0
    for ch in letters:
        n = n * 26 + (ord(ch) - 64)
    return n - 1


def _idx_to_col_letters(idx):
    s = ""
    idx += 1
    while idx:
        idx, r = divmod(idx - 1, 26)
        s = chr(65 + r) + s
    return s


def _parse_sheet_cells(xml, strings):
    """
    Parse a worksheet XML into:
      cells : {(row1based, col0based): value_str}
      maxrow, maxcol
    Handles t="s" (shared), t="str"/"inlineStr", and bare numeric values.
    """
    cells = {}
    maxrow = 0
    maxcol = 0
    for rn, body in re.findall(r'<row[^>]*\br="(\d+)"[^>]*>(.*?)</row>', xml, re.S):
        for cm in re.finditer(r'<c r="([A-Z]+)(\d+)"([^>/]*)(?:/>|>(.*?)</c>)', body, re.S):
            col_letters, row_s, attrs, inner = cm.group(1), cm.group(2), cm.group(3) or "", cm.group(4) or ""
            row = int(row_s)
            col = _col_letters_to_idx(col_letters)
            val = ""
            t = ""
            tm = re.search(r'\bt="([^"]+)"', attrs)
            if tm:
                t = tm.group(1)
            if t == "inlineStr":
                parts = re.findall(r"<t[^>]*>(.*?)</t>", inner, re.S)
                val = unescape("".join(parts)) if parts else ""
            else:
                vm = re.search(r"<v>(.*?)</v>", inner, re.S)
                if vm is not None:
                    raw = vm.group(1)
                    if t == "s":
                        val = strings[int(raw)]
                    else:
                        val = unescape(raw)
            cells[(row, col)] = val
            if row > maxrow:
                maxrow = row
            if col > maxcol:
                maxcol = col
    return cells, maxrow, maxcol


def _reassemble_meta(z, strings):
    """Reassemble the hidden cowork_meta JSON from whichever worksheet holds it, and json.loads it.

    LAYOUT-AGNOSTIC (hardened 2026-06-20): Transom writes the meta JSON split across MANY small cells;
    when a user OPENS+SAVES the workbook in Excel, Excel RE-CHUNKS it into a DIFFERENT cell layout (e.g.
    the whole blob collapses into 2 big cells A1/A2). The chunks are all still present and the data is
    intact (a manual row-major concat json.loads's fine) — only the OLD reassembler assumed Transom's
    original cell layout + document order, so it failed on the Excel layout. This version reuses the same
    robust cell parser as the data sheet (_parse_sheet_cells: handles shared/inline/numeric) and
    concatenates EVERY cell value in true ROW-MAJOR order — sorted by (row asc, col asc) — regardless of
    chunk count or XML document order. That reconstructs the JSON for BOTH Transom's original layout AND
    any Excel re-save.
    """
    # The hidden meta sheet is conventionally sheet2.xml; fall back to scanning every worksheet for one
    # whose row-major concatenation parses as the meta JSON (a dict with "sheets").
    candidates = ["xl/worksheets/sheet2.xml"]
    candidates += [n for n in z.namelist()
                   if re.match(r"xl/worksheets/sheet\d+\.xml$", n) and n not in candidates]
    last_err = None
    for name in candidates:
        try:
            xml = _read_zip_text(z, name)
        except KeyError:
            continue
        cells, _maxrow, _maxcol = _parse_sheet_cells(xml, strings)
        if not cells:
            continue
        # TRUE row-major: sort by (row, col) so the blob is reconstructed in reading order no matter how
        # Excel (re)distributed it across cells. (col0 leading-pad cell is empty in Transom's layout — an
        # empty string concatenates harmlessly.)
        blob = "".join(cells[k] for k in sorted(cells.keys())).strip()
        if not blob:
            continue
        try:
            meta = json.loads(blob)
            if isinstance(meta, dict) and "sheets" in meta:
                return meta, name
        except Exception as e:  # noqa
            last_err = e
            continue
    raise SystemExit("FATAL: could not reassemble/parse cowork_meta JSON from any worksheet. "
                     "Last error: %r" % (last_err,))


def load_workbook(path, sheet_name=None):
    z = zipfile.ZipFile(path)
    ss_xml = _read_zip_text(z, "xl/sharedStrings.xml") if "xl/sharedStrings.xml" in z.namelist() else ""
    strings = _parse_shared_strings(ss_xml)
    meta, meta_sheet = _reassemble_meta(z, strings)

    # pick the data sheet (sheet1 is conventional; it is the one that is NOT the meta sheet)
    data_candidates = [n for n in z.namelist()
                       if re.match(r"xl/worksheets/sheet\d+\.xml$", n) and n != meta_sheet]
    data_candidates.sort()
    if not data_candidates:
        raise SystemExit("FATAL: no data worksheet found in workbook.")
    vis_xml = _read_zip_text(z, data_candidates[0])
    cells, maxrow, maxcol = _parse_sheet_cells(vis_xml, strings)

    sheets = meta.get("sheets", [])
    if not sheets:
        raise SystemExit("FATAL: meta has no sheets[].")
    if sheet_name:
        match = [s for s in sheets if (s.get("sheetName") == sheet_name or s.get("scheduleName") == sheet_name)]
        if not match:
            names = ", ".join(repr(s.get("sheetName")) for s in sheets)
            raise SystemExit("FATAL: sheet %r not found. Available: %s" % (sheet_name, names))
        sheet = match[0]
    else:
        if len(sheets) > 1:
            names = ", ".join(repr(s.get("sheetName")) for s in sheets)
            raise SystemExit("FATAL: workbook has %d sheets (%s); pass --sheet NAME." % (len(sheets), names))
        sheet = sheets[0]

    return meta, sheet, cells, maxrow, maxcol


# --------------------------------------------------------------------------- #
#  Offset detection (excelRow -> xlsx 1-based row)
# --------------------------------------------------------------------------- #
def _anchor_col_index(sheet, cells, maxrow, maxcol, meta_anchor):
    """Find the column index of the anchor sentinel header in the visible sheet."""
    anchor = (sheet.get("anchorColumnHeader") or meta_anchor or ANCHOR_DEFAULT)
    for r in range(1, min(maxrow, 5) + 1):       # header band is in the first few rows
        for c in range(0, maxcol + 1):
            if cells.get((r, c), "") == anchor:
                return c, anchor
    return None, anchor


def detect_offset(sheet, cells, anchor_col):
    """
    Try offsets 0,1,2,-1 ; choose the one that makes the anchor-column value equal
    meta.uniqueId for the MOST data rows. Returns (offset, matched, total, votes).
    """
    data_rows = [r for r in sheet["rows"]
                 if r.get("kind") in ("element", "type", "group") and r.get("uniqueId")]
    votes = {}
    for off in (1, 0, 2, -1, 3):
        hits = 0
        for r in data_rows:
            if cells.get((r["excelRow"] + off, anchor_col)) == r["uniqueId"]:
                hits += 1
        votes[off] = hits
    best = max(votes, key=lambda o: (votes[o], -abs(o)))
    return best, votes[best], len(data_rows), votes


# --------------------------------------------------------------------------- #
#  Live Revit bridge
# --------------------------------------------------------------------------- #
class Bridge(object):
    def __init__(self, url):
        self.url = url

    def run(self, code, description="transom_verify"):
        payload = json.dumps({"code": code, "description": description}).encode("utf-8")
        last = None
        for attempt in range(RETRIES):
            try:
                req = urllib.request.Request(self.url, data=payload,
                                             headers={"Content-Type": "application/json"})
                # T1 fix: CALL_TIMEOUT (was 120). A wedged bridge no longer stalls minutes per call.
                with urllib.request.urlopen(req, timeout=CALL_TIMEOUT) as resp:
                    body = resp.read().decode("utf-8", "replace")
            except socket.timeout:
                # T1 fix: surface a TIMEOUT distinctly + visibly, rather than looping mutely.
                last = "socket TIMEOUT after %ss" % CALL_TIMEOUT
                sys.stderr.write("  [bridge TIMEOUT] %s (attempt %d/%d)\n" % (description, attempt + 1, RETRIES))
                sys.stderr.flush()
                continue
            except urllib.error.HTTPError as e:
                body = e.read().decode("utf-8", "replace") if e.fp else ""
                last = "HTTP %s: %s" % (e.code, body[:300])
                if not body:
                    continue
            except Exception as e:  # noqa
                last = "conn error: %r" % (e,)
                continue
            if not body.strip():
                last = "empty response body"
                continue
            try:
                obj = json.loads(body)
            except Exception:
                last = "non-JSON response: %s" % body[:200]
                continue
            if obj.get("status") == "success":
                return obj.get("output", "")
            # script-side error
            err = obj.get("error") or obj.get("traceback") or body[:400]
            last = "bridge error: %s" % err
        # T1 fix: raise a typed exception so batch_read can convert it to a LABELED INCOMPLETE
        # (a partial, explicitly-flagged result) instead of letting the whole run die with 0 bytes.
        raise BridgeError("bridge call failed after %d attempts: %s" % (RETRIES, last))

    def ping(self):
        out = self.run("print('ok')", "ping")
        return out.strip() == "ok"


def select_live_doc(bridge, source_model, override):
    """Return the document Title to read from (selected among OPEN docs)."""
    list_code = (
        "import json\n"
        "app = __revit__.Application\n"
        "ds = []\n"
        "for d in app.Documents:\n"
        "    try:\n"
        "        ds.append(d.Title)\n"
        "    except: pass\n"
        "print(json.dumps(ds))\n")

    def _list_titles():
        out = bridge.run(list_code, "list docs")
        try:
            raw = json.loads(out.strip())
        except Exception:
            return None
        if not isinstance(raw, list):
            return None
        # Keep only well-formed string titles; a bridge hiccup can yield nested/garbled
        # entries (a title parsed as a list) which previously crashed on .lower().
        return [t for t in raw if isinstance(t, str) and t.strip()]

    # A transient bridge garble can return a malformed doc list; retry before failing.
    titles = None
    for attempt in range(MISSING_RETRIES + 1):
        titles = _list_titles()
        if titles:
            break
        if attempt < MISSING_RETRIES:
            time.sleep(MISSING_RETRY_SLEEP)
    if not titles:
        raise SystemExit("FATAL: could not list open Revit documents (empty/garbled after retries).")

    want_title = (source_model or {}).get("title")
    if override:
        cand = [t for t in titles if override.lower() in t.lower()]
        if not cand:
            raise SystemExit("FATAL: --doc %r matched none of the open docs: %s" % (override, titles))
        return cand[0], titles
    if want_title:
        if want_title in titles:
            return want_title, titles
        cand = [t for t in titles if want_title.lower() in t.lower() or t.lower() in want_title.lower()]
        if cand:
            return cand[0], titles
    # last resort: single open doc
    if len(titles) == 1:
        return titles[0], titles
    raise SystemExit(
        "FATAL: cannot identify the live doc. Workbook sourceModel.title=%r ; open docs=%s. "
        "Re-run with --doc \"<substring>\"." % (want_title, titles))


def _doc_selector_code(doc_title):
    """IronPython snippet that sets `doc` to the chosen document by exact title."""
    t = json.dumps(doc_title)  # safe escaping
    return ("app = __revit__.Application\n"
            "doc = None\n"
            "for d in app.Documents:\n"
            "    if d.Title == %s:\n"
            "        doc = d\n"
            "        break\n"
            "if doc is None:\n"
            "    raise Exception('target doc not open: ' + %s)\n" % (t, t))


def _read_doc_identity(bridge, doc_title):
    """T3: read the chosen doc's CreationGUID + PathName so a wrong-doc read is visible. Best-effort."""
    sel = _doc_selector_code(doc_title)
    code = ("import json\n" + sel +
            "g = None\n"
            "try:\n"
            "    g = str(doc.CreationGUID)\n"
            "except: g = None\n"
            "p = None\n"
            "try:\n"
            "    p = doc.PathName\n"
            "except: p = None\n"
            "print(json.dumps({'guid': g, 'path': p, 'title': doc.Title}))\n")
    try:
        out = bridge.run(code, "doc identity")
        return json.loads(out.strip())
    except Exception:
        return None


def batch_read(bridge, doc_title, requests, transom_names=None):
    """
    requests: list of (uid, parameterId).
    transom_names: optional {(uid,pid): "Header (Transom)"|"Header (Transom)_instance"} — the 2a/2b
        shared param NAME to ALSO read on the element (T2). If the original pid's value doesn't match
        but this param does, value_matches treats it as a (Transom-param) MATCH.
    Returns {(uid, pid): {'as_string','as_value','status', 'tx_string','tx_value'}}.
    De-dupes and chunks. Streams per-batch progress to stderr (T1). A wedged/over-budget read yields
    BRIDGEFAIL/INCOMPLETE-labeled keys, never a silent 0-byte death.
    """
    transom_names = transom_names or {}
    uniq = []
    seen = set()
    for uid, pid in requests:
        k = (uid, pid)
        if k not in seen:
            seen.add(k)
            uniq.append(k)

    result = {}
    sel = _doc_selector_code(doc_title)

    def _read_keys(keys, label):
        """Read a list of (uid,pid) keys in BATCH-sized chunks; return {key: rec}."""
        out_map = {}
        nchunks = (len(keys) + BATCH - 1) // BATCH
        for i in range(0, len(keys), BATCH):
            # T1: hard wall-clock ceiling — defer the rest as BRIDGEFAIL (→ INCOMPLETE), don't run forever.
            if _budget_exceeded():
                sys.stderr.write("  [BUDGET] total read budget %ss exceeded at batch %d/%d; deferring remaining\n"
                                 % (TOTAL_BUDGET, i // BATCH + 1, nchunks))
                sys.stderr.flush()
                for (u, p) in keys[i:]:
                    out_map.setdefault((u, p), {"as_string": None, "as_value": None,
                                                "tx_string": None, "tx_value": None, "status": "BRIDGEFAIL"})
                break
            chunk = keys[i:i + BATCH]
            # carry the optional Transom param-name CANDIDATES per key (list; [] if none). The bridge matches
            # whichever candidate Definition.Name actually exists on the element (option-agnostic 2a/2b).
            reqs_json = json.dumps([[u, p, transom_names.get((u, p), [])] for (u, p) in chunk])
            code = (
                "import json\n"
                + sel +
                "reqs = json.loads('''" + reqs_json + "''')\n"
                "cache = {}\n"
                "res = []\n"
                "for uid, pid, txns in reqs:\n"
                "    if uid in cache:\n"
                "        el = cache[uid]\n"
                "    else:\n"
                "        el = doc.GetElement(uid)\n"
                "        cache[uid] = el\n"
                "    if el is None:\n"
                "        res.append([uid, pid, None, None, 'MISSING', None, None])\n"
                "        continue\n"
                "    sval = None\n"
                "    vval = None\n"
                "    found = False\n"
                "    txs = None\n"
                "    txv = None\n"
                "    for p in el.Parameters:\n"
                "        if p.Id.IntegerValue == pid:\n"
                "            found = True\n"
                "            try:\n"
                "                sval = p.AsString()\n"
                "            except: sval = None\n"
                "            try:\n"
                "                vval = p.AsValueString()\n"
                "            except: vval = None\n"
                # Match the (Transom) candidate name — but only ONCE (first NON-EMPTY match wins, so a later
                # same-named empty/duplicate param can't clobber a good read; mirrors the type-loop's break).
                "        if txns and not (txs or txv) and p.Definition is not None and (p.Definition.Name in txns):\n"
                "            try:\n"
                "                txs = p.AsString()\n"
                "            except: txs = None\n"
                "            try:\n"
                "                txv = p.AsValueString()\n"
                "            except: txv = None\n"
                # FIX (c3-025): opt-2a creates a TYPE param on the element's TYPE — not the instance. If the
                # "(Transom)" param wasn't found on `el` itself, ALSO search its type element. (opt-2b makes an
                # instance param, found on el directly.) So we try-both-targets, matching the candidate name on
                # whichever element carries it. Without this, an instance-bound column's 2a param is never found.
                "    if txns and not (txs or txv):\n"
                "        try:\n"
                "            tid = el.GetTypeId()\n"
                "        except: tid = None\n"
                "        if tid is not None and tid.IntegerValue > 0:\n"
                "            te = doc.GetElement(tid)\n"
                "            if te is not None:\n"
                "                for p in te.Parameters:\n"
                "                    if p.Definition is not None and (p.Definition.Name in txns):\n"
                "                        try:\n"
                "                            txs = p.AsString()\n"
                "                        except: txs = None\n"
                "                        try:\n"
                "                            txv = p.AsValueString()\n"
                "                        except: txv = None\n"
                "                        break\n"
                "    res.append([uid, pid, sval, vval, ('OK' if found else 'NOPARAM'), txs, txv])\n"
                "print(json.dumps(res))\n"
            )
            # Per-chunk retry: the bridge intermittently returns garbled output for a batch (a stray
            # 'ok' ping-reply in a data slot, or a truncated/non-JSON body). Re-issue + validate shape;
            # a BridgeError (timeout/exhausted retries) is CAUGHT here → BRIDGEFAIL keys, never a crash.
            rows = None
            for catt in range(CHUNK_RETRIES + 1):
                try:
                    out = bridge.run(code, "%s %d/%d" % (label, i // BATCH + 1, nchunks))
                except BridgeError as be:
                    sys.stderr.write("  [chunk-bridgefail] batch %d/%d: %s\n"
                                     % (i // BATCH + 1, nchunks, be))
                    sys.stderr.flush()
                    out = ""
                try:
                    parsed = json.loads(out.strip())
                except Exception:
                    parsed = None
                # Validate: list of 7-tuples [uid,pid,sval,vval,status,txs,txv].
                if (isinstance(parsed, list)
                        and all(isinstance(r, list) and len(r) == 7 for r in parsed)):
                    rows = parsed
                    break
                if catt < CHUNK_RETRIES:
                    sys.stderr.write(
                        "  [chunk-retry %d/%d] garbled/empty batch reply (%r); re-reading\n"
                        % (catt + 1, CHUNK_RETRIES, (out or "")[:60]))
                    sys.stderr.flush()
                    time.sleep(MISSING_RETRY_SLEEP)
            if rows is None:
                sys.stderr.write(
                    "  [chunk-fail] batch %d/%d unreadable after %d tries; deferring %d key(s)\n"
                    % (i // BATCH + 1, nchunks, CHUNK_RETRIES, len(chunk)))
                sys.stderr.flush()
                for (u, p) in chunk:
                    out_map[(u, p)] = {"as_string": None, "as_value": None,
                                       "tx_string": None, "tx_value": None, "status": "BRIDGEFAIL"}
                continue
            for uid, pid, sval, vval, status, txs, txv in rows:
                out_map[(uid, pid)] = {"as_string": sval, "as_value": vval,
                                       "tx_string": txs, "tx_value": txv, "status": status}
            # T1: per-batch progress to stderr so a stall is VISIBLE (you can see WHERE it hung).
            sys.stderr.write("  [%s] batch %d/%d ok (%d keys, %.0fs elapsed)\n"
                             % (label, i // BATCH + 1, nchunks, len(chunk), time.time() - _RUN_START))
            sys.stderr.flush()
        return out_map

    # Initial pass.
    result.update(_read_keys(uniq, "batch read"))

    # MISSING-retry: a transient bridge/doc-state hiccup can make doc.GetElement(uid)
    # return None for a whole BATCH of valid elements (observed: contiguous row bands
    # going <MISSING> on instance reads during an apply, all clearing on a re-read).
    # Re-read only the not-OK keys up to MISSING_RETRIES times before believing MISSING.
    for attempt in range(1, MISSING_RETRIES + 1):
        stale = [k for k in uniq if result.get(k, {}).get("status") != "OK"]
        if not stale:
            break
        time.sleep(MISSING_RETRY_SLEEP)
        sys.stderr.write(
            "  [retry %d/%d] re-reading %d non-OK element(s) (transient-dropout guard)\n"
            % (attempt, MISSING_RETRIES, len(stale)))
        for k, rec in _read_keys(stale, "missing-retry").items():
            # Only upgrade away from a non-OK status; never overwrite a good read with a worse one.
            if rec.get("status") == "OK" or result.get(k, {}).get("status") != "OK":
                result[k] = rec
    return result


# --------------------------------------------------------------------------- #
#  Comparison logic
# --------------------------------------------------------------------------- #
def _norm(v):
    if v is None:
        return ""
    return v.strip() if isinstance(v, str) else str(v)


def value_matches(cell, live):
    """
    cell  : workbook cell string (already unescaped)
    live  : {'as_string','as_value','status','tx_string','tx_value'}
    Returns (matches:bool, display_live:str, via:str)
      via = "" (original param) | "Transom" (2a/2b param) | the status token on failure.
    Matches if the workbook cell equals the original param's AsString/AsValueString (T4: a length/numeric
    param has AsString=None → AsValueString is used), OR — for 2a/2b — the "(Transom)" param's value (T2).
    Empty cell matches when both live readings are empty/None.
    """
    c = _norm(cell)
    s = _norm(live.get("as_string"))
    v = _norm(live.get("as_value"))
    txs = _norm(live.get("tx_string"))
    txv = _norm(live.get("tx_value"))
    # choose the most informative live display (T4: AsValueString when AsString is null/empty)
    display = live.get("as_string")
    if display is None or display == "":
        display = live.get("as_value")
    display = _norm(display)
    if live.get("status") != "OK":
        return False, "<%s>" % live.get("status"), live.get("status")
    # ORIGINAL param first (the ordinary case + the common case). If it matches, done — and a stale/leftover
    # "(Transom)" param can NEVER override a correct original match.
    if c == s or c == v:
        return True, display, ""
    # T2 (code-review fix 2026-06-22): consult the "(Transom)" param ONLY as a fallback, and ONLY when it holds
    # a NON-EMPTY value (`txs or txv` truthy). opt-2a/2b NULLS the original and writes the value to "(Transom)",
    # so a real 2a/2b landing reaches here (original didn't match → empty/stale) and the (Transom) value is
    # non-empty → MATCH (Transom-param). This is immune to BOTH (a) a stale leftover "(Transom)" param from a
    # prior arm (it can only resolve a NON-match, never flip a correct original match) and (b) an EMPTY/unset
    # leftover "(Transom)" param (the truthy guard ignores ""). Needs no knowledge of which option ran.
    # (Earlier over-correction made any present (Transom) authoritative+exclusive via `is not None` → a stale or
    # empty leftover param produced false DIFFERs on correct unedited cells. Reverted to original-first ordering;
    # the real T2 fixes — dual candidate names + type-element search — are retained.)
    if (txs or txv) and (c == txs or c == txv):
        tx_disp = txs if txs else txv
        return True, tx_disp, "Transom"
    return False, display, ""


def build_comparisons(sheet, cells, offset, anchor_col, col_filter=None, param_filter=None):
    """
    Returns:
      rows_out : list of dicts (one per cell) with all fields
      read_reqs: list of (uid, pid) to fetch from the bridge
      problems : list of structural warnings
    """
    columns = sheet["columns"]
    by_idx = {c["col"]: c for c in columns}
    problems = []

    # which columns to evaluate
    sel_cols = []
    for c in columns:
        if not c.get("writable"):
            continue
        if param_filter is not None and c.get("parameterId") != param_filter:
            continue
        if col_filter is not None:
            cf = col_filter.lower()
            if cf not in (c.get("header", "") or "").lower() and cf not in (c.get("fieldName", "") or "").lower():
                continue
        sel_cols.append(c)

    rows_out = []
    read_reqs = []
    transom_names = {}   # T2: {(uid,pid): "Header (Transom)[_instance]"}
    data_rows = [r for r in sheet["rows"] if r.get("kind") in ("element", "type", "group")]
    for r in data_rows:
        xlsx_row = r["excelRow"] + offset
        # structural self-check: anchor must equal uniqueId
        anchor_val = cells.get((xlsx_row, anchor_col))
        if r.get("uniqueId") and anchor_val != r.get("uniqueId"):
            problems.append("row excelRow=%s xlsx=%s anchor mismatch (got %r want %r)"
                            % (r["excelRow"], xlsx_row, anchor_val, r.get("uniqueId")))
        row_bindings = r.get("bindings") or {}
        for c in sel_cols:
            col_idx = c["col"]
            cell_val = cells.get((xlsx_row, col_idx), "")
            # binding: column-level, cross-checked against per-row bindings
            binding = c.get("binding")
            rb = row_bindings.get(str(col_idx))
            if rb and rb != binding:
                binding = rb  # per-row override wins (defensive)
            pid = c.get("parameterId")

            if binding == "type":
                targets = [r.get("uniqueId")] if r.get("uniqueId") else []
            else:
                targets = list(r.get("instanceIds") or [])
            targets = [t for t in targets if t]
            # T2 (FIXED 2026-06-22): the 2a/2b shared param's Definition.Name. Source uses sample.Field →
            # "{fieldName} (Transom)", with an "_instance" suffix ONLY when the created param is INSTANCE-bound.
            # CRITICAL: the suffix depends on the OPTION CHOSEN, not the column's binding — option 2a creates a
            # TYPE param (NO suffix), option 2b creates an INSTANCE param ("_instance"). The verifier can't know
            # from the workbook which option was applied, so it offers BOTH candidate names and matches whichever
            # param actually exists on the element. (Earlier bug: keyed the suffix off the column binding —
            # TYPE col is instance-bound so it looked for "..._instance", but 2a made the no-suffix TYPE param →
            # false DIFFER. Importer.cs:3258-3260.) code3 c3-021/c3-025.
            base = c.get("fieldName") or c.get("header") or ""
            tx_cands = ["%s (Transom)" % base, "%s (Transom)_instance" % base] if base else []
            for t in targets:
                read_reqs.append((t, pid))
                if tx_cands:
                    transom_names[(t, pid)] = tx_cands

            rows_out.append({
                "xlsx_row": xlsx_row,
                "excel_row": r["excelRow"],
                "kind": r.get("kind"),
                "header": c.get("header"),
                "field": c.get("fieldName"),
                "param": pid,
                "binding": binding,
                "cell": cell_val,
                "targets": targets,
                "col_idx": col_idx,
            })
    return rows_out, read_reqs, problems, sel_cols, transom_names


def evaluate(rows_out, reads):
    """Fill each row dict with live value(s) + verdict using fetched `reads`."""
    for ro in rows_out:
        pid = ro["param"]
        targets = ro["targets"]
        if not targets:
            ro["verdict"] = "NO_TARGET"
            ro["live"] = ""
            ro["all_match"] = None
            ro["instances_disagree"] = False
            ro["fan"] = ""
            ro["via"] = ""
            continue
        per = []
        displays = []
        statuses = []
        vias = []
        for t in targets:
            live = reads.get((t, pid), {"status": "MISSING", "as_string": None, "as_value": None,
                                        "tx_string": None, "tx_value": None})
            m, disp, via = value_matches(ro["cell"], live)
            per.append(m)
            displays.append(disp)
            statuses.append(live.get("status"))
            if via:
                vias.append(via)
        # do the instances agree among themselves on the live value?
        uniq_disp = set(displays)
        disagree = (len(targets) > 1 and len(uniq_disp) > 1)
        n = len(targets)
        n_ok = sum(1 for s in statuses if s == "OK")
        n_match = sum(1 for b in per if b)
        all_ok = (n_ok == n)
        all_match = all(per) and all_ok
        ro["live"] = displays[0] if len(uniq_disp) == 1 else (" | ".join(sorted(uniq_disp)))
        ro["all_match"] = all_match
        ro["instances_disagree"] = disagree
        # T3: surface the instance fan so a type-row cell that fans to N instances isn't just "1 cell",
        # and a PARTIAL apply (k/N) is visible instead of hidden behind one verdict. (c3-023: show
        # single-instance rows as "1/1 inst" too, for uniform display.)
        ro["fan"] = ("%d/%d inst" % (n_match, n)) if (n >= 1 and ro["binding"] != "type") else ""
        # T2: note when the match came via the "(Transom)" 2a/2b param (all targets matched via Transom).
        ro["via"] = "Transom" if (vias and all(v == "Transom" for v in vias)) else ""
        # T5f: distinguish a read failure (MISSING/BRIDGEFAIL) from a real value change.
        n_unread = sum(1 for st in statuses if st in ("MISSING", "BRIDGEFAIL"))
        if all_match and not disagree:
            ro["verdict"] = "MATCH" + (" (Transom-param)" if ro["via"] == "Transom" else "")
        elif n_unread == n:
            # wholly unread → surface the read-failure status itself (n_unread==n guarantees statuses[0] is one).
            ro["verdict"] = statuses[0]
        else:
            ro["verdict"] = "DIFFER"  # real differ, or partial-unread (flagged via fan + status)
    return rows_out


# --------------------------------------------------------------------------- #
#  Output
# --------------------------------------------------------------------------- #
def _clip(s, n):
    s = "" if s is None else str(s)
    s = s.replace("\n", "\\n")
    return s if len(s) <= n else s[:n - 1] + "…"


def print_report(rows_out, sel_cols, show_all, problems, header_info):
    print(header_info)
    print("")

    # per-column diff counts
    from collections import OrderedDict
    counts = OrderedDict()
    for c in sel_cols:
        counts[(c["col"], c.get("header"), c.get("parameterId"))] = {"diff": 0, "total": 0,
                                                                     "disagree": 0, "missing": 0}
    def _is_diff(v):
        # MATCH / MATCH (Transom-param) / NO_TARGET are NOT diffs; everything else (DIFFER/MISSING/BRIDGEFAIL) is.
        return not (v == "NO_TARGET" or v.startswith("MATCH"))
    for ro in rows_out:
        key = (ro["col_idx"], ro["header"], ro["param"])
        if key not in counts:
            counts[key] = {"diff": 0, "total": 0, "disagree": 0, "missing": 0}
        counts[key]["total"] += 1
        v = ro["verdict"]
        if _is_diff(v):
            counts[key]["diff"] += 1
        if ro.get("instances_disagree"):
            counts[key]["disagree"] += 1
        if v == "MISSING" or (isinstance(ro.get("live"), str) and ro["live"].startswith("<MISSING")):
            counts[key]["missing"] += 1
        if v == "BRIDGEFAIL" or (isinstance(ro.get("live"), str) and ro["live"].startswith("<BRIDGEFAIL")):
            counts[key].setdefault("bridgefail", 0)
            counts[key]["bridgefail"] += 1
        if ro.get("via") == "Transom":
            counts[key].setdefault("transom", 0)
            counts[key]["transom"] += 1

    shown = [ro for ro in rows_out if show_all or _is_diff(ro["verdict"])]

    if shown:
        hdr = ("%-5s %-3s %-14s %-9s %-22s %-22s %-12s %-9s"
               % ("row", "col", "header", "binding", "workbook_value", "live_value", "fan", "verdict"))
        print(hdr)
        print("-" * len(hdr))
        # group by column for readability
        shown.sort(key=lambda r: (r["col_idx"], r["xlsx_row"]))
        for ro in shown:
            flag = ""
            if ro.get("instances_disagree"):
                flag = " [instances-disagree]"
            print("%-5s %-3s %-14s %-9s %-22s %-22s %-12s %-9s%s" % (
                ro["xlsx_row"], ro["col_idx"], _clip(ro["header"], 14), ro["binding"] or "",
                _clip(ro["cell"], 22), _clip(ro["live"], 22), ro.get("fan", "") or "", ro["verdict"], flag))
        print("")
    else:
        print("(no differing cells)\n" if not show_all else "(no cells)\n")

    # summary
    print("=== PER-COLUMN DIFFERENCE COUNTS ===")
    print("%-14s %-9s %-10s %7s / %-7s  %s" % ("header", "param", "binding", "diffs", "total", "notes"))
    bind_by_col = {c["col"]: c.get("binding") for c in sel_cols}
    total_diff = 0
    total_bridgefail = 0
    for (col, header, param), v in counts.items():
        total_diff += v["diff"]
        total_bridgefail += v.get("bridgefail", 0)
        notes = []
        if v["disagree"]:
            notes.append("%d instances-disagree" % v["disagree"])
        if v["missing"]:
            notes.append("%d missing-elem" % v["missing"])
        if v.get("bridgefail"):
            notes.append("%d BRIDGEFAIL(unread)" % v["bridgefail"])
        print("%-14s %-9s %-10s %7d / %-7d  %s" % (
            _clip(header, 14), str(param), bind_by_col.get(col, ""), v["diff"], v["total"],
            "; ".join(notes)))
    print("")
    print("TOTAL DIFFERING CELLS: %d" % total_diff)
    if total_bridgefail:
        print("")
        print("!!! INCOMPLETE: %d cell(s) could NOT be read from the bridge after retries "
              "(status BRIDGEFAIL). These are counted as DIFFER but are UNVERIFIED, not data "
              "changes. Re-run when the bridge is stable before trusting this verdict." % total_bridgefail)
    if problems:
        print("")
        print("!!! STRUCTURAL WARNINGS (%d) -- results may be unreliable:" % len(problems))
        for p in problems[:20]:
            print("   " + p)
    return total_diff


# --------------------------------------------------------------------------- #
#  Main
# --------------------------------------------------------------------------- #
def main():
    ap = argparse.ArgumentParser(description="Verify a Transom workbook against the live Revit model.")
    ap.add_argument("workbook", help="path to the .xlsx workbook")
    ap.add_argument("--all", action="store_true", help="dump every cell, not just DIFFERing ones")
    ap.add_argument("--column", help="filter to columns whose header/field contains this (case-insensitive)")
    ap.add_argument("--param", type=int, help="filter to a single parameterId (e.g. -1001203)")
    ap.add_argument("--doc", help="substring to pick the live document among open docs (overrides auto-match)")
    ap.add_argument("--sheet", help="schedule/sheet name (required if workbook has >1 sheet)")
    ap.add_argument("--bridge", default=DEFAULT_BRIDGE, help="pyRevit routes execute_code URL")
    ap.add_argument("--no-live", action="store_true", help="parse only; skip the live comparison (debug)")
    args = ap.parse_args()

    meta, sheet, cells, maxrow, maxcol = load_workbook(args.workbook, args.sheet)

    anchor_col, anchor = _anchor_col_index(sheet, cells, maxrow, maxcol, meta.get("anchorSentinel"))
    if anchor_col is None:
        raise SystemExit("FATAL: anchor sentinel %r not found in the visible sheet header band." % anchor)

    offset, matched, total, votes = detect_offset(sheet, cells, anchor_col)
    if total == 0:
        raise SystemExit("FATAL: no data rows with a uniqueId in meta -- cannot verify.")
    if matched < total:
        # require a clean, consistent mapping
        raise SystemExit(
            "FATAL: could not establish a consistent excelRow->xlsx-row offset.\n"
            "  best offset=%+d matched %d/%d rows (votes=%s).\n"
            "  Refusing to emit possibly-wrong results."
            % (offset, matched, total, votes))

    n_cols = sum(1 for c in sheet["columns"] if c.get("writable"))
    header_info = (
        "Workbook : %s\n"
        "Schedule : %s   (category=%s)\n"
        "SourceMdl: title=%r guid=%s\n"
        "Anchor   : %r at column index %d (%s)\n"
        "Offset   : meta.excelRow %+d -> xlsx row   [matched %d/%d data rows; votes=%s]\n"
        "Columns  : %d writable / %d total ;  data rows: %d"
        % (args.workbook, sheet.get("sheetName"), sheet.get("category"),
           (meta.get("sourceModel") or {}).get("title"), (meta.get("sourceModel") or {}).get("guid"),
           anchor, anchor_col, _idx_to_col_letters(anchor_col),
           offset, matched, total, votes,
           n_cols, len(sheet["columns"]),
           sum(1 for r in sheet["rows"] if r.get("kind") in ("element", "type", "group"))))

    rows_out, read_reqs, problems, sel_cols, transom_names = build_comparisons(
        sheet, cells, offset, anchor_col, col_filter=args.column, param_filter=args.param)

    if not sel_cols:
        raise SystemExit("No writable columns matched the filter (--column/--param).")

    if args.no_live:
        print(header_info)
        print("\n[--no-live] parsed %d cells across %d columns; %d live reads would be issued."
              % (len(rows_out), len(sel_cols), len(set(read_reqs))))
        if problems:
            print("STRUCTURAL WARNINGS:\n  " + "\n  ".join(problems[:20]))
        return

    bridge = Bridge(args.bridge)
    if not bridge.ping():
        raise SystemExit("FATAL: bridge ping failed at %s" % args.bridge)

    doc_title, all_titles = select_live_doc(bridge, meta.get("sourceModel"), args.doc)
    header_info += "\nLiveDoc  : %r   (open docs: %s)" % (doc_title, all_titles)
    # T3: read the CHOSEN doc's real CreationGUID + PathName and PRINT them, so a wrong-doc / title-collision
    # read is VISIBLE (this is what would have caught the false "clean" reads). Assert guid == meta guid.
    live_id = _read_doc_identity(bridge, doc_title)
    meta_guid = (meta.get("sourceModel") or {}).get("guid")
    if live_id:
        guid_ok = (live_id.get("guid") == meta_guid)
        header_info += ("\nLiveID   : GUID=%s | Path=%r  [%s]"
                        % (live_id.get("guid"), live_id.get("path"),
                           "GUID OK" if guid_ok else "⚠ GUID MISMATCH vs workbook %s — verify you are reading the RIGHT model" % meta_guid))
    if all_titles and args.doc:
        matches = [t for t in all_titles if args.doc.lower() in t.lower()]
        if len(matches) > 1:
            header_info += ("\n⚠ --doc %r matched %d open docs %s — chose %r; confirm it's the intended one"
                            % (args.doc, len(matches), matches, doc_title))

    # T1: print the header BEFORE the live reads start (so a stall isn't a total blackout), then stream
    # per-batch progress to stderr inside batch_read.
    sys.stderr.write(header_info + "\n--- live read starting (%d reads, BATCH=%d, call-timeout=%ss, budget=%ss) ---\n"
                     % (len(set(read_reqs)), BATCH, CALL_TIMEOUT, TOTAL_BUDGET))
    sys.stderr.flush()

    reads = batch_read(bridge, doc_title, read_reqs, transom_names=transom_names)
    evaluate(rows_out, reads)

    total_diff = print_report(rows_out, sel_cols, args.all, problems, header_info)
    # T5d: distinct exit codes — 0 clean, 1 real diffs, 2 INCOMPLETE (any cell unread: BRIDGEFAIL/MISSING-all).
    incomplete = any(r.get("status") == "BRIDGEFAIL" for r in reads.values())
    if incomplete:
        sys.exit(EXIT_INCOMPLETE)
    sys.exit(EXIT_CLEAN if (total_diff == 0 and not problems) else EXIT_DIFF)


if __name__ == "__main__":
    main()
