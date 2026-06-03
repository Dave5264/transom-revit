"""One-shot MCP tool caller: initialize, then call one tool, print its result.
Usage: python call_tool.py <toolName> [jsonArgs]
"""
import json, subprocess, sys, os

EXE = r"bin\Debug\net8.0\RevitClickHelperMcp.exe"
tool = sys.argv[1]
targs = json.loads(sys.argv[2]) if len(sys.argv) > 2 else {}

p = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=os.environ)

def send(m):
    d = json.dumps(m).encode()
    p.stdin.write(f"Content-Length: {len(d)}\r\n\r\n".encode("ascii")); p.stdin.write(d); p.stdin.flush()

def read():
    h, line = {}, b""
    while True:
        c = p.stdout.read(1)
        if not c: return None
        line += c
        if line.endswith(b"\r\n"):
            if line == b"\r\n": break
            k, _, v = line[:-2].partition(b":"); h[k.decode().strip().lower()] = v.decode().strip(); line = b""
    return json.loads(p.stdout.read(int(h["content-length"])).decode())

send({"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"t","version":"0"}}})
read()
send({"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":tool,"arguments":targs}})
r = read()
# strip image data for readability
for item in r.get("result",{}).get("content",[]):
    if item.get("type")=="image": item["data"]="<image %d b64 chars>"%len(item["data"])
print(json.dumps(r, indent=2))
p.stdin.close(); p.wait(timeout=5)
