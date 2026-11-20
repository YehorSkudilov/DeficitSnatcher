"""
Camoufox for Deficit Snatcher: a Playwright server the API connects to (BrowserPool: Firefox.ConnectAsync), so Camoufox is
the app's browser itself (checks, checkouts, the AI), not a page fetcher. Camoufox is a Firefox build that hides
automation and gives the browser a consistent, believable fingerprint.

    ws://camoufox:4444/camoufox      (Settings -> Checking -> Camoufox)

Each check or checkout gets its own context (the API puts the page's saved cookies and storage in it). The fingerprint is
made once per start; the container restarts it if the browser dies. Only reachable inside Docker's network.
"""

import os
import json
import queue
import sys
import threading
import urllib.request

from camoufox.server import launch_server


class ForwardingStream:
    """Mirror each stdout/stderr line to the API while retaining normal container output."""

    def __init__(self, original, forward):
        self.original = original
        self.forward = forward
        self.buffer = ""
        self.lock = threading.Lock()
        self.encoding = getattr(original, "encoding", "utf-8")

    def write(self, text):
        written = self.original.write(text)
        self.original.flush()
        with self.lock:
            self.buffer += text
            while "\n" in self.buffer:
                line, self.buffer = self.buffer.split("\n", 1)
                if line.strip():
                    self.forward(line.rstrip("\r"))
        return len(text) if written is None else written

    def flush(self):
        self.original.flush()

    def isatty(self):
        return self.original.isatty()

    def fileno(self):
        return self.original.fileno()


class LogForwarder:
    def __init__(self, url):
        self.url = url
        self.lines = queue.Queue(maxsize=500)
        threading.Thread(target=self.run, name="log-forwarder", daemon=True).start()

    def __call__(self, line):
        try:
            self.lines.put_nowait(line)
        except queue.Full:
            pass

    def run(self):
        while True:
            line = self.lines.get()
            payload = json.dumps({"line": line}).encode("utf-8")
            request = urllib.request.Request(self.url, data=payload, headers={"Content-Type": "application/json"})
            for attempt in range(15):
                try:
                    with urllib.request.urlopen(request, timeout=3):
                        break
                except Exception:
                    if attempt < 14:
                        threading.Event().wait(1)


log_url = os.environ.get("LOG_FORWARD_URL", "")
if log_url:
    forward = LogForwarder(log_url)
    sys.stdout = ForwardingStream(sys.stdout, forward)
    sys.stderr = ForwardingStream(sys.stderr, forward)

options = {
    "port": int(os.environ.get("PORT", "4444")),
    "ws_path": os.environ.get("WS_PATH", "camoufox"),
    # Every address, not only localhost (Playwright's default here): the API reaches it from another container.
    "host": os.environ.get("HOST", "0.0.0.0"),
    # "virtual": a real (headed) Firefox on a virtual screen (Xvfb), like the API's Chromium. "true": headless.
    "headless": {"virtual": "virtual", "true": True, "false": False}.get(os.environ.get("HEADLESS", "virtual").lower(), "virtual"),
    "humanize": True,
    "locale": os.environ.get("LOCALE", "en-CA"),
}
if os.environ.get("CAMOUFOX_EXECUTABLE"):
    options["executable_path"] = os.environ["CAMOUFOX_EXECUTABLE"]
# A proxy for the whole browser (each context can also have its own: the API passes Settings -> Proxy). With one, geoip
# matches the fingerprint's timezone, locale and location to the proxy's address.
if os.environ.get("PROXY_SERVER"):
    options["proxy"] = {k: v for k, v in {
        "server": os.environ["PROXY_SERVER"],
        "username": os.environ.get("PROXY_USERNAME"),
        "password": os.environ.get("PROXY_PASSWORD"),
    }.items() if v}
    options["geoip"] = True

launch_server(**options)
