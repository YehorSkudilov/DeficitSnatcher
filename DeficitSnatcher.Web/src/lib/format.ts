export function money(value: number | null | undefined, currency = "CAD"): string {
  if (value === null || value === undefined) return "—";
  try {
    return new Intl.NumberFormat("en-CA", { style: "currency", currency, currencyDisplay: "narrowSymbol" }).format(value);
  } catch {
    return `${value.toFixed(2)} ${currency}`;
  }
}

export function ago(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "never";
  const s = Math.round((now - new Date(iso).getTime()) / 1000);
  if (s < 0) return `in ${until(iso, now)}`;
  if (s < 5) return "just now";
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.floor(s / 60)}m ago`;
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`;
  return `${Math.floor(s / 86400)}d ago`;
}

export function until(iso: string | null | undefined, now = Date.now()): string {
  if (!iso) return "—";
  const s = Math.round((new Date(iso).getTime() - now) / 1000);
  if (s <= 0) return "now";
  const clock = localClockTime(iso);
  if (s < 60) return `${s}s at ${clock}`;
  if (s < 3600) return `${Math.floor(s / 60)}m ${s % 60}s at ${clock}`;
  return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m at ${clock}`;
}

export function dateTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  return new Date(iso).toLocaleString("en-CA", { dateStyle: "medium", timeStyle: "short" });
}

export function localClockTime(iso: string | null | undefined): string {
  if (!iso) return "";
  return new Date(iso).toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
}

/** Common favicon and touch-icon locations on the store's own origin. */
export function storeIconSources(url: string): string[] {
  try {
    const origin = new URL(url).origin;
    return ["/favicon.ico", "/favicon.png", "/apple-touch-icon.png", "/apple-touch-icon-precomposed.png"]
      .map((path) => `${origin}${path}`);
  } catch {
    return [];
  }
}

export function host(url: string): string {
  try {
    return new URL(url).host.replace(/^www\./, "");
  } catch {
    return url;
  }
}
