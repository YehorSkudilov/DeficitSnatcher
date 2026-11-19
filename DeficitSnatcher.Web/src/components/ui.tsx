"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError, type EventLevel, type PurchaseStatus, type Site } from "@/lib/api";

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
  const cls = { Running: "info", DryRun: "warn", Succeeded: "good", Failed: "bad" }[status];
  const text = { Running: "Running", DryRun: "Dry run", Succeeded: "Bought", Failed: "Failed" }[status];
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

export function Check({ label, checked, onChange }: { label: React.ReactNode; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <label className="check">
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
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
