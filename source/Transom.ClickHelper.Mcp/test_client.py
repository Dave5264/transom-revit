"""Minimal MCP stdio test client for the Click Helper MCP server.

Frames JSON-RPC 2.0 messages with Content-Length headers, sends a handshake plus a
few tools/call requests, and prints the responses (truncating big base64 image data).
"""
import json
import subprocess
import sys
import os

EXE = sys.argv[1] if len(sys.argv) > 1 else r"bin\Debug\net8.0\Transom.ClickHelper.Mcp.exe"

proc = subprocess.Popen(
    [EXE],
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    env=os.environ,
)


def send(msg):
    data = json.dumps(msg).encode("utf-8")
    proc.stdin.write(f"Content-Length: {len(data)}\r\n\r\n".encode("ascii"))
    proc.stdin.write(data)
    proc.stdin.flush()


def read():
    # read headers
    headers = {}
    line = b""
    while True:
        ch = proc.stdout.read(1)
        if not ch:
            return None
        line += ch
        if line.endswith(b"\r\n"):
            if line == b"\r\n":
                break
            k, _, v = line[:-2].partition(b":")
            headers[k.decode().strip().lower()] = v.decode().strip()
            line = b""
    n = int(headers["content-length"])
    body = proc.stdout.read(n)
    return json.loads(body.decode("utf-8"))


def trunc(obj):
    """Shorten long base64 image data so the print is readable."""
    if isinstance(obj, dict):
        return {k: ("<%d b64 chars>" % len(v) if k == "data" and isinstance(v, str) else trunc(v))
                for k, v in obj.items()}
    if isinstance(obj, list):
        return [trunc(x) for x in obj]
    return obj


def call(rid, method, params=None):
    m = {"jsonrpc": "2.0", "id": rid, "method": method}
    if params is not None:
        m["params"] = params
    send(m)
    resp = read()
    print(f"\n=== {method} {params.get('name') if params and 'name' in params else ''} ===")
    print(json.dumps(trunc(resp), indent=2)[:2000])
    return resp


call(1, "initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "test", "version": "0"}})
tl = call(2, "tools/list")
print("\nTOOL NAMES:", [t["name"] for t in tl["result"]["tools"]])
call(3, "tools/call", {"name": "revit_status", "arguments": {}})
call(4, "tools/call", {"name": "revit_find", "arguments": {"text": "modify"}})
call(5, "tools/call", {"name": "revit_screenshot", "arguments": {}})

proc.stdin.close()
try:
    err = proc.stderr.read().decode("utf-8", "replace")
    if err:
        print("\n--- server stderr ---\n" + err[:1500])
except Exception:
    pass
proc.wait(timeout=5)
