"""
Camoufox page renderer for Deficit Snatcher.

A small HTTP service in front of Camoufox (a Firefox build that hides automation and gives each context a believable
fingerprint). The API asks it to open a page, waits for the page's script to run, and gets back the rendered HTML plus
the context's storage state (cookies and localStorage), which it sends back on the next check of the same page so the
store sees the same returning visitor.

    POST /render   {"url", "timeoutMs"?, "settleMs"?, "storageState"?, "cookies"?, "proxy"?}
                -> {"html", "status", "finalUrl", "storageState"}       or {"error"} with a 4xx/5xx
    GET  /health   -> {"ok": true, "connected": bool}

It talks to Camoufox through Camoufox's own Python package, so the Playwright version it needs never has to match the
API's (.NET) Playwright. It's only reachable inside Docker's network; nothing is published.
"""

import asyncio
import json
import logging
import os
import time

from aiohttp import web
from camoufox.async_api import AsyncCamoufox

log = logging.getLogger("camoufox-renderer")
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")

PORT = int(os.environ.get("PORT", "8000"))
MAX_PAGES = max(1, int(os.environ.get("MAX_PAGES", "2")))
# "virtual": a real (headed) Firefox on Xvfb, like the API's Chromium; "true": headless.
HEADLESS = os.environ.get("HEADLESS", "virtual")
EXECUTABLE = os.environ.get("CAMOUFOX_EXECUTABLE") or None
# A fresh browser now and then: a long-lived one slowly gathers memory.
RESTART_AFTER = int(os.environ.get("RESTART_AFTER_PAGES", "200"))
# Only behind a proxy that re-signs TLS with its own certificate authority.
IGNORE_HTTPS_ERRORS = os.environ.get("IGNORE_HTTPS_ERRORS", "").lower() in ("1", "true", "yes")

_lock = asyncio.Lock()
_pages = asyncio.Semaphore(MAX_PAGES)
_manager = None
_browser = None
_served = 0
_active = 0


async def _start():
    global _manager, _browser, _served
    headless = {"virtual": "virtual", "true": True, "false": False}.get(HEADLESS.lower(), "virtual")
    options = {"headless": headless, "humanize": True, "locale": "en-CA"}
    if EXECUTABLE:
        options["executable_path"] = EXECUTABLE
    log.info("Starting Camoufox (headless=%s)", headless)
    _manager = AsyncCamoufox(**options)
    _browser = await _manager.__aenter__()
    _served = 0


async def _stop():
    global _manager, _browser
    manager, _manager, _browser = _manager, None, None
    if manager is not None:
        try:
            await manager.__aexit__(None, None, None)
        except Exception as e:  # already gone
            log.warning("Closing Camoufox: %s", e)


async def browser():
    """The browser, started (again) when it isn't running or has served its share of pages and is idle."""
    async with _lock:
        if _browser is not None and _browser.is_connected() and not (_served >= RESTART_AFTER and _active == 0):
            return _browser
        await _stop()
        await _start()
        return _browser


def _cookie(c):
    return {"name": c["name"], "value": c.get("value", ""), "domain": c["domain"], "path": c.get("path") or "/"}


async def render(request: web.Request) -> web.Response:
    global _served, _active
    try:
        body = await request.json()
    except json.JSONDecodeError:
        return web.json_response({"error": "The request isn't JSON."}, status=400)
    url = body.get("url")
    if not isinstance(url, str) or not url.startswith(("http://", "https://")):
        return web.json_response({"error": "url must be an http(s) address."}, status=400)
    timeout_ms = int(body.get("timeoutMs") or 30000)
    settle_ms = int(body.get("settleMs") or 8000)

    async with _pages:
        _active += 1
        context = None
        try:
            for attempt in (1, 2):
                b = await browser()
                options = {"ignore_https_errors": True} if IGNORE_HTTPS_ERRORS else {}
                if body.get("storageState"):
                    options["storage_state"] = body["storageState"]
                if body.get("proxy"):
                    p = body["proxy"]
                    options["proxy"] = {k: v for k, v in {"server": p.get("server"), "username": p.get("username"),
                                                          "password": p.get("password")}.items() if v}
                try:
                    context = await b.new_context(**options)
                    break
                except Exception as e:
                    # The browser died under us: once more with a new one.
                    if attempt == 2 or b.is_connected():
                        raise
                    log.warning("Camoufox went away (%s); restarting", e)
            cookies = [_cookie(c) for c in body.get("cookies") or [] if c.get("name") and c.get("domain")]
            if cookies:
                await context.add_cookies(cookies)
            page = await context.new_page()
            started = time.monotonic()
            response = await page.goto(url, wait_until="domcontentloaded", timeout=timeout_ms)
            try:
                await page.wait_for_load_state("networkidle", timeout=settle_ms)
            except Exception:
                pass  # pages that keep polling never go idle; what's there by now is enough
            html = await page.content()
            state = await context.storage_state()
            _served += 1
            log.info("Rendered %s (%s) in %.1fs", url, response.status if response else "-", time.monotonic() - started)
            return web.json_response({
                "html": html,
                "status": response.status if response else 0,
                "finalUrl": page.url,
                "storageState": state,
            })
        except Exception as e:
            message = str(e).split("\n")[0]
            log.warning("Render of %s failed: %s", url, message)
            status = 504 if "Timeout" in type(e).__name__ or "Timeout" in message else 502
            return web.json_response({"error": message}, status=status)
        finally:
            _active -= 1
            if context is not None:
                try:
                    await context.close()
                except Exception:
                    pass


async def health(_: web.Request) -> web.Response:
    return web.json_response({"ok": True, "connected": _browser is not None and _browser.is_connected()})


async def on_cleanup(_: web.Application):
    await _stop()


def main():
    app = web.Application(client_max_size=64 * 1024 * 1024)
    app.router.add_post("/render", render)
    app.router.add_get("/health", health)
    app.on_cleanup.append(on_cleanup)
    web.run_app(app, port=PORT, access_log=None)


if __name__ == "__main__":
    main()
