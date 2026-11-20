"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError, type EventLevel, type PurchaseStatus, type ScanOutcome, type Site } from "@/lib/api";
import { storeIconSources } from "@/lib/format";

/** Loads with `load`, again every `refreshMs` (if set) and whenever `reload` is called. */
export function useData<T>(load: () => Promise<T>, deps: unknown[], refreshMs?: number) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const loadRef = useRef(load);
  useEffect(() => {
    loadRef.current = load;
  });

  const reload = useCallback(async () => {
    try {
      const d = await loadRef.current();
      setData(d);
      setError(null);
    } catch (e) {
      if (!(e instanceof ApiError && e.status === 401)) setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    let alive = true;
    const run = async () => {
      if (alive) await reload();
    };
    void run();
    if (!refreshMs) return () => void (alive = false);
    const id = setInterval(() => {
      if (document.visibilityState === "visible") void run();
    }, refreshMs);
    return () => {
      alive = false;
      clearInterval(id);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, refreshMs, reload]);

  return { data, error, reload, setData };
}

/** Re-renders every second, for "12s ago" texts. */
export function useNow(ms = 1000) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), ms);
    return () => clearInterval(id);
  }, [ms]);
  return now;
}

/** Runs an action, keeping "busy" while it does and its error after. */
export function useAction() {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const run = useCallback(async <T,>(f: () => Promise<T>): Promise<T | undefined> => {
    setBusy(true);
    setError(null);
    try {
      return await f();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      return undefined;
    } finally {
      setBusy(false);
    }
  }, []);
  return { busy, error, setError, run };
}

export function ErrorNote({ error }: { error: string | null | undefined }) {
  return error ? <div className="notice bad">{error}</div> : null;
}

export function StockBadge({ site }: { site: Pick<Site, "lastInStock" | "lastCheckedAt" | "lastError" | "consecutiveErrors"> }) {
  if (site.consecutiveErrors > 0) return <span className="badge bad">Error ×{site.consecutiveErrors}</span>;
  if (!site.lastCheckedAt) return <span className="badge">Not checked</span>;
  if (site.lastInStock === true) return <span className="badge good">In stock</span>;
  if (site.lastInStock === false) return <span className="badge">Out of stock</span>;
  return <span className="badge warn">Unknown</span>;
}

export function PurchaseBadge({ status }: { status: PurchaseStatus }) {
  const cls = { Running: "info", DryRun: "warn", Succeeded: "good", Failed: "bad", Interrupted: "bad" }[status];
  const text = { Running: "Running…", DryRun: "Dry run", Succeeded: "Bought", Failed: "Failed", Interrupted: "Interrupted" }[status];
  return <span className={`badge ${cls}`}>{text}</span>;
}

export function LevelDot({ level }: { level: EventLevel }) {
  return <span className={`dot ${level}`} />;
}

export function Field({ label, hint, children }: { label: string; hint?: React.ReactNode; children: React.ReactNode }) {
  return (
    <label className="field">
      <span>{label}</span>
      {children}
      {hint && <small>{hint}</small>}
    </label>
  );
}

export function Check({ label, checked, onChange, disabled }: { label: React.ReactNode; checked: boolean; onChange: (v: boolean) => void; disabled?: boolean }) {
  return (
    <label className="check">
      <input type="checkbox" checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
      <span>{label}</span>
    </label>
  );
}

/** A number input that allows being empty while typing. */
export function NumberInput({
  value,
  onChange,
  step,
  min,
  placeholder,
}: {
  value: number | null;
  onChange: (v: number | null) => void;
  step?: number;
  min?: number;
  placeholder?: string;
}) {
  return (
    <input
      type="number"
      value={value ?? ""}
      step={step}
      min={min}
      placeholder={placeholder}
      onChange={(e) => onChange(e.target.value === "" ? null : Number(e.target.value))}
    />
  );
}

export function OutcomeBadge({ outcome }: { outcome: ScanOutcome }) {
  const cls = { InStock: "good", OutOfStock: "", Unknown: "warn", Error: "bad" }[outcome];
  const text = { InStock: "In stock", OutOfStock: "Out of stock", Unknown: "Unknown", Error: "Error" }[outcome];
  return <span className={`badge ${cls}`}>{text}</span>;
}

/** An image from the API (it needs the token, so it's fetched rather than linked). Click: full size. */
export function AuthImage({ path, alt, className }: { path: string; alt: string; className?: string }) {
  const [src, setSrc] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);
  const [open, setOpen] = useState(false);
  useEffect(() => {
    let url: string | null = null;
    let alive = true;
    api
      .get<Blob>(path)
      .then((b) => {
        url = URL.createObjectURL(b);
        if (alive) setSrc(url);
      })
      .catch(() => alive && setFailed(true));
    return () => {
      alive = false;
      if (url) URL.revokeObjectURL(url);
    };
  }, [path]);
  if (failed) return <span className="faint small">(screenshot unavailable)</span>;
  if (!src) return <span className={`shot-loading ${className ?? ""}`} />;
  return (
    <>
      {/* eslint-disable-next-line @next/next/no-img-element -- a blob from the API */}
      <img src={src} alt={alt} className={className} onClick={() => setOpen(true)} style={{ cursor: "zoom-in" }} />
      {open && (
        <div className="lightbox" onClick={() => setOpen(false)}>
          {/* eslint-disable-next-line @next/next/no-img-element -- a blob from the API */}
          <img src={src} alt={alt} />
          <div className="lightbox-caption">{alt}</div>
        </div>
      )}
    </>
  );
}

/** Downloads a file from the API (with the token). */
export async function download(path: string, name: string) {
  const blob = await api.get<Blob>(path);
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

const ExpandedKey = "ds.expandedProducts.v2";

/** Which products are expanded (remembered in this browser, shared by the Dashboard and Products). */
export function useCollapsed() {
  const [ids, setIds] = useState<number[]>(() => {
    try {
      return typeof localStorage === "undefined" ? [] : (JSON.parse(localStorage.getItem(ExpandedKey) ?? "[]") as number[]);
    } catch {
      return [];
    }
  });
  const save = useCallback((next: number[]) => {
    setIds(next);
    try {
      localStorage.setItem(ExpandedKey, JSON.stringify(next));
    } catch {
      // Not remembered, still works.
    }
  }, []);
  return {
    isCollapsed: (id: number) => !ids.includes(id),
    toggle: (id: number) => save(ids.includes(id) ? ids.filter((x) => x !== id) : [...ids, id]),
    setAll: (all: number[], collapsed: boolean) => save(collapsed ? [] : all),
    allExpanded: (all: number[]) => all.length > 0 && all.every((id) => ids.includes(id)),
  };
}

/** The ▸/▾ that folds a product up. */
export function CollapseButton({ collapsed, onClick }: { collapsed: boolean; onClick: () => void }) {
  return (
    <button
      type="button"
      className="btn small ghost collapse-btn"
      aria-expanded={!collapsed}
      title={collapsed ? "Show pages" : "Hide pages"}
      onClick={(e) => {
        e.preventDefault();
        e.stopPropagation();
        onClick();
      }}
    >
      <span className={`chevron${collapsed ? "" : " open"}`}>▸</span>
    </button>
  );
}

/** One line for a folded product: its pages, how many are in stock. */
export function PagesSummary({ sites }: { sites: Pick<Site, "name" | "url" | "lastInStock" | "enabled">[] }) {
  const inStock = sites.filter((s) => s.lastInStock && s.enabled).length;
  return (
    <div className="small muted row" style={{ gap: 8 }}>
      <span>{sites.length} page{sites.length === 1 ? "" : "s"}</span>
      {sites.slice(0, 6).map((site) => <MiniStoreIcon key={site.url} name={site.name} url={site.url} />)}
      {inStock > 0 ? (
        <>
          {" · "}
          <span className="badge good">{inStock} in stock</span>
        </>
      ) : (
        " · none in stock"
      )}
    </div>
  );
}

function MiniStoreIcon({ name, url }: { name: string; url: string }) {
  const sources = storeIconSources(url);
  const [sourceIndex, setSourceIndex] = useState(0);
  return (
    <span className="store-icon mini" title={name}>
      {sourceIndex < sources.length ? <img src={sources[sourceIndex]} alt="" onError={() => setSourceIndex((i) => i + 1)} /> : <span>{name.trim().slice(0, 1).toUpperCase()}</span>}
    </span>
  );
}
