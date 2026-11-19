"use client";

import { useEffect, useState, useSyncExternalStore } from "react";
import { api, getToken, setSignedOutHandler, setToken, type Dashboard as DashboardData } from "@/lib/api";
import { Dashboard } from "./Dashboard";
import { Products } from "./Products";
import { Sites } from "./Sites";
import { SiteEditor } from "./SiteEditor";
import { PurchaseDetail, Purchases } from "./Purchases";
import { Scans } from "./Scans";
import { Deploy } from "./Deploy";
import { Activity } from "./Activity";
import { SettingsPage } from "./Settings";
import { Login } from "./Login";
import { useData } from "./ui";

/** The screen from the address's #part: #sites, #site/12, #site/new… */
function subscribeHash(f: () => void) {
  window.addEventListener("hashchange", f);
  return () => window.removeEventListener("hashchange", f);
}
const readHash = () => window.location.hash.replace(/^#\/?/, "") || "dashboard";

export function go(route: string) {
  window.location.hash = route;
}

const nav = [
  ["dashboard", "Dashboard"],
  ["products", "Price ranges"],
  ["sites", "Sites"],
  ["scans", "Scans"],
  ["purchases", "Checkouts"],
  ["activity", "Activity"],
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
  const section = screen === "site" ? "sites" : screen === "purchase" ? "purchases" : screen;

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
        {screen === "dashboard" && <Dashboard />}
        {screen === "products" && <Products />}
        {screen === "sites" && <Sites />}
        {screen === "site" && (
          <SiteEditor
            key={arg}
            id={arg === "new" ? null : Number(arg)}
            productId={Number(new URLSearchParams(search ?? "").get("product")) || undefined}
          />
        )}
        {screen === "scans" && <Scans key={search} initial={search} />}
        {screen === "purchases" && <Purchases />}
        {screen === "purchase" && <PurchaseDetail key={arg} id={Number(arg)} />}
        {screen === "deploy" && <Deploy />}
        {screen === "activity" && <Activity />}
        {screen === "settings" && <SettingsPage />}
        {!nav.some(([k]) => k === screen) && screen !== "site" && screen !== "purchase" && <p className="muted">Nothing here. <a href="#dashboard">Dashboard</a></p>}
      </main>
    </div>
  );
}
