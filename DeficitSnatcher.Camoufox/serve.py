"""
Camoufox for Deficit Snatcher: a Playwright server the API connects to (BrowserPool: Firefox.ConnectAsync), so Camoufox is
the app's browser itself (checks, checkouts, the AI), not a page fetcher. Camoufox is a Firefox build that hides
automation and gives the browser a consistent, believable fingerprint.

    ws://camoufox:4444/camoufox      (Settings -> Checking -> Camoufox)

Each check or checkout gets its own context (the API puts the page's saved cookies and storage in it). The fingerprint is
made once per start; the container restarts it if the browser dies. Only reachable inside Docker's network.
"""

import os

from camoufox.server import launch_server

options = {
    "port": int(os.environ.get("PORT", "4444")),
    "ws_path": os.environ.get("WS_PATH", "camoufox"),
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
