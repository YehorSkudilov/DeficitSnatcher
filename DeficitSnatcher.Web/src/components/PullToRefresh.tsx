"use client";

import { useEffect, useRef } from "react";

/** Pulling this far down (after resistance) and letting go reloads the page. */
const threshold = 70;

/** True for anything scrolled down between the touch and the page: then pulling down scrolls that, not refreshes. */
function insideScrolled(target: EventTarget | null) {
  for (let el = target instanceof Element ? target : null; el && el !== document.body; el = el.parentElement)
    if (el.scrollTop > 0) return true;
  return false;
}

/**
 * Pull down at the top of the page to reload it, in the Android app's web view (which, unlike a phone's browser, has no
 * pull-to-refresh of its own). Elsewhere it does nothing, so it never doubles up with the browser's.
 */
export function PullToRefresh() {
  const ring = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!/; wv\)/.test(navigator.userAgent)) return;
    let startY: number | null = null;
    let startX = 0;
    let pull = 0;

    const show = (distance: number, spinning = false) => {
      const el = ring.current;
      if (!el) return;
      el.style.transform = `translate(-50%, ${Math.min(distance, threshold * 1.4) - 50}px) rotate(${distance * 3}deg)`;
      el.style.opacity = String(Math.min(distance / threshold, 1));
      el.classList.toggle("ready", distance >= threshold);
      el.classList.toggle("spinning", spinning);
    };

    const start = (e: TouchEvent) => {
      startY = e.touches.length === 1 && window.scrollY <= 0 && !insideScrolled(e.target) ? e.touches[0].clientY : null;
      startX = e.touches[0]?.clientX ?? 0;
      pull = 0;
    };
    const move = (e: TouchEvent) => {
      if (startY === null) return;
      const dy = e.touches[0].clientY - startY;
      const dx = Math.abs(e.touches[0].clientX - startX);
      // Sideways (a wide table) or scrolling down: not a pull.
      if (window.scrollY > 0 || (pull === 0 && dx > Math.abs(dy))) {
        startY = null;
        show(0);
        return;
      }
      pull = Math.max(0, dy * 0.5);
      show(pull);
    };
    const end = () => {
      if (startY === null) return;
      startY = null;
      if (pull >= threshold) {
        show(threshold, true);
        window.location.reload();
      } else show(0);
    };

    window.addEventListener("touchstart", start, { passive: true });
    window.addEventListener("touchmove", move, { passive: true });
    window.addEventListener("touchend", end);
    window.addEventListener("touchcancel", end);
    return () => {
      window.removeEventListener("touchstart", start);
      window.removeEventListener("touchmove", move);
      window.removeEventListener("touchend", end);
      window.removeEventListener("touchcancel", end);
    };
  }, []);

  return <div ref={ring} className="pull-ring" aria-hidden />;
}
