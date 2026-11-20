"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import { api, getToken, setSignedOutHandler, setToken, type Dashboard as DashboardData } from "@/lib/api";
import { Dashboard } from "./Dashboard";
import { ProductPage, Products } from "./Products";
import { SiteEditor } from "./SiteEditor";
import { PurchaseDetail } from "./Purchases";
import { Deploy } from "./Deploy";
import { Helpers } from "./Helpers";
import { SettingsPage } from "./Settings";
import { Login } from "./Login";
import { PendingCodes } from "./PendingCodes";
import { useData } from "./ui";

/** The screen from the address's #part: #products, #product/3, #site/12, #purchase/7… */
function subscribeHash(f: () => void) {
  window.addEventListener("hashchange", f);
  return () => window.removeEventListener("hashchange", f);
}
// Addresses from before the menu was merged (bookmarks, notification links) still land in the right place.
// Logs now live in each product and page.
const moved: Record<string, string> = {
  sites: "products",
  scans: "products",
  purchases: "products",
  activity: "products",
  logs: "products",
  "logs/scans": "products",
  "logs/checkouts": "products",
  "logs/activity": "products",
};
const readHash = () => {
  const route = window.location.hash.replace(/^#\/?/, "") || "dashboard";
  const [path, search] = route.split("?");
  return moved[path] ? moved[path] + (search ? `?${search}` : "") : route;
};

export function go(route: string) {
  window.location.hash = route;
}

const nav = [
  ["dashboard", "Dashboard"],
  ["products", "Products"],
  ["helpers", "Helpers"],
  ["settings", "Settings"],
  ["deploy", "Deploy"],
] as const;

export function App() {
  const route = useSyncExternalStore(subscribeHash, readHash, () => "");
  const [signedIn, setSignedIn] = useState<boolean | null>(null);

  useEffect(() => {
    setSignedOutHandler(() => setSignedIn(false));
    // eslint-disable-next-line react-hooks/set-state-in-effect -- the token lives in localStorage, only readable here
    setSignedIn(!!getToken());
  }, []);

  if (signedIn === null || route === "") return null;
  if (!signedIn) return <Login onSignedIn={() => setSignedIn(true)} />;
  return <Shell route={route} onSignOut={() => (setToken(null), setSignedIn(false))} />;
}

function Shell({ route, onSignOut }: { route: string; onSignOut: () => void }) {
  // The nav's light: red when auto-buy is armed, green while tracking.
  const { data: status } = useData(() => api.get<DashboardData>("/api/dashboard"), [], 10000);
  const [path, search] = route.split("?");
  const [screen, arg] = path.split("/");
  const section = ["site", "product", "purchase"].includes(screen) ? "products" : screen;

  return (
    <div className="shell">
      <nav className="nav">
        <div className="brand" title={status?.autoBuyArmed ? "Auto-buy armed" : status?.trackingEnabled ? "Tracking" : "Paused"}>
          <i className={`brand-dot ${status?.autoBuyArmed ? "armed" : status?.trackingEnabled ? "tracking" : ""}`} />
          <span>Deficit Snatcher</span>
        </div>
        {nav.map(([key, label]) => (
          <a key={key} href={`#${key}`} className={section === key ? "active" : ""}>
            {label}
          </a>
        ))}
        <div className="spacer" />
        <a href="#" onClick={(e) => (e.preventDefault(), onSignOut())}>
          Sign out
        </a>
      </nav>
      <main className="main">
        <PendingCodes />
        {screen === "dashboard" && <Dashboard />}
        {screen === "products" && <Products />}
        {screen === "product" && <ProductPage key={arg} id={arg === "new" ? null : Number(arg)} />}
        {screen === "site" && (
          <SiteEditor
            key={arg}
            id={arg === "new" ? null : Number(arg)}
            productId={Number(new URLSearchParams(search ?? "").get("product")) || undefined}
          />
        )}
        {screen === "purchase" && <PurchaseDetail key={arg} id={Number(arg)} />}
        {screen === "helpers" && <Helpers />}
        {screen === "deploy" && <Deploy />}
        {screen === "settings" && <SettingsPage />}
        {!nav.some(([k]) => k === screen) && !["site", "product", "purchase"].includes(screen) && <p className="muted">Nothing here. <a href="#dashboard">Dashboard</a></p>}
      </main>
    </div>
  );
}
