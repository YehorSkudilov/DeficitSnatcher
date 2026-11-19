// The API's calls (DeficitSnatcher.Api). In Docker it's this same site under /api (nginx.conf passes it through);
// NEXT_PUBLIC_API_URL (at build time) points elsewhere, e.g. http://localhost:5210 for npm run dev.

export const apiUrl = (process.env.NEXT_PUBLIC_API_URL || "").replace(/\/+$/, "");

const TokenKey = "ds.token";

export function getToken(): string | null {
  try {
    return localStorage.getItem(TokenKey);
  } catch {
    return null;
  }
}

export function setToken(token: string | null) {
  try {
    if (token) localStorage.setItem(TokenKey, token);
    else localStorage.removeItem(TokenKey);
  } catch {
    // Private mode: signed in for this page only.
  }
}

export class ApiError extends Error {
  constructor(
    message: string,
    public status: number,
  ) {
    super(message);
  }
}

/** Called when the API says the token is no longer good (the app shows sign-in again). */
let onSignedOut: () => void = () => {};
export const setSignedOutHandler = (f: () => void) => (onSignedOut = f);

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = {};
  const token = getToken();
  if (token) headers.Authorization = `Bearer ${token}`;
  if (body !== undefined) headers["Content-Type"] = "application/json";
  let response: Response;
  try {
    response = await fetch(`${apiUrl}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  } catch {
    throw new ApiError("Can't reach the server.", 0);
  }
  if (response.status === 401 && path !== "/api/auth/login") {
    setToken(null);
    onSignedOut();
    throw new ApiError("Signed out.", 401);
  }
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const problem = await response.json();
      message = problem.detail || problem.title || message;
    } catch {
      // Not JSON.
    }
    throw new ApiError(message, response.status);
  }
  if (response.status === 204) return undefined as T;
  const type = response.headers.get("content-type") || "";
  return (type.includes("json") ? await response.json() : await response.blob()) as T;
}

export const api = {
  get: <T>(path: string) => request<T>("GET", path),
  post: <T>(path: string, body?: unknown) => request<T>("POST", path, body ?? {}),
  put: <T>(path: string, body: unknown) => request<T>("PUT", path, body),
  del: <T = void>(path: string) => request<T>("DELETE", path),
};

// ---- Types (the API's DTOs) ----

export type Product = {
  id: number;
  name: string;
  enabled: boolean;
  minPrice: number;
  maxPrice: number;
  maxQuantity: number;
  quantityPerOrder: number;
  requiredKeywords: string;
  excludedKeywords: string;
  bought: number;
  sites: number;
  createdAt: string;
};

export type ProductInput = Omit<Product, "id" | "bought" | "sites" | "createdAt">;

export const SiteKinds = ["Auto", "Shopify", "Html", "Browser"] as const;
export type SiteKind = (typeof SiteKinds)[number];

export const StepActions = ["goto", "click", "fill", "type", "select", "check", "press", "wait", "waitFor", "waitForUrl", "expectText"] as const;
export type StepAction = (typeof StepActions)[number];

export type CheckoutStep = {
  action: StepAction;
  selector?: string;
  value?: string;
  frame?: string;
  final?: boolean;
  optional?: boolean;
  timeoutMs?: number;
  note?: string;
};

export type Site = {
  id: number;
  productId: number;
  productName: string;
  name: string;
  url: string;
  kind: SiteKind;
  enabled: boolean;
  intervalSeconds: number | null;
  shippingCost: number;
  priceSelector: string | null;
  titleSelector: string | null;
  inStockSelector: string | null;
  outOfStockText: string | null;
  variantId: string | null;
  autoBuy: boolean;
  checkoutSteps: CheckoutStep[];
  hasCookies: boolean;
  lastCheckedAt: string | null;
  lastChangedAt: string | null;
  lastPrice: number | null;
  lastInStock: boolean | null;
  lastTitle: string | null;
  lastError: string | null;
  consecutiveErrors: number;
  match: boolean;
  verdict: string;
  nextCheckAt: string | null;
  checking: boolean;
};

export type SiteInput = {
  productId: number;
  name: string;
  url: string;
  kind: SiteKind;
  enabled: boolean;
  intervalSeconds: number | null;
  shippingCost: number;
  priceSelector: string | null;
  titleSelector: string | null;
  inStockSelector: string | null;
  outOfStockText: string | null;
  variantId: string | null;
  autoBuy: boolean;
  checkoutSteps: CheckoutStep[];
  /** null: keep the saved cookies; "": remove them. */
  cookies: string | null;
};

export type PageResult = {
  price: number | null;
  inStock: boolean | null;
  title: string | null;
  currency: string | null;
  variantId: string | null;
  notes: string[];
};

export type Verdict = { match: boolean; totalPrice: number | null; reason: string };
export type ProbeResponse = { result: PageResult | null; verdict: Verdict | null; error: string | null; buyMessage: string | null };
export type BuyOutcome = { started: boolean; message: string; purchaseId: number | null; status: PurchaseStatus | null };
export type HistoryPoint = { at: string; price: number | null; inStock: boolean };

export type PurchaseStatus = "Running" | "DryRun" | "Succeeded" | "Failed" | "Interrupted";
export const PurchaseStatuses: PurchaseStatus[] = ["Succeeded", "Failed", "DryRun", "Running", "Interrupted"];

export type Purchase = {
  id: number;
  siteId: number;
  siteName: string;
  productId: number;
  productName: string;
  startedAt: string;
  finishedAt: string | null;
  price: number;
  quantity: number;
  status: PurchaseStatus;
  message: string;
  dryRun: boolean;
  manual: boolean;
  shots: number;
};

export type StepRecord = {
  index: number;
  action: string;
  description: string;
  /** ok, failed, skipped (optional and failed), notRun. */
  status: "ok" | "failed" | "skipped" | "notRun";
  startedAt: string | null;
  ms: number | null;
  url: string | null;
  error: string | null;
  shotId: number | null;
  final: boolean;
  optional: boolean;
};

export type Shot = { id: number; stepIndex: number | null; label: string; at: string };

export type PurchaseDetail = {
  purchase: Purchase;
  siteUrl: string;
  context: string;
  steps: StepRecord[];
  log: string;
  error: string | null;
  browserLog: string;
  finalUrl: string | null;
  hasHtml: boolean;
  shots: Shot[];
  scan: { id: number; at: string } | null;
};

export type ScanOutcome = "InStock" | "OutOfStock" | "Unknown" | "Error";

export type Scan = {
  id: number;
  siteId: number;
  siteName: string;
  productId: number;
  productName: string;
  at: string;
  durationMs: number;
  outcome: ScanOutcome;
  price: number | null;
  totalPrice: number | null;
  title: string | null;
  match: boolean;
  verdict: string;
  error: string | null;
  notes: string | null;
  action: string | null;
  purchaseId: number | null;
  manual: boolean;
};

export type ScanCounts = { all: number; inStock: number; outOfStock: number; unknown: number; error: number; match: number; bought: number };
export type ScansPage = { scans: Scan[]; counts: ScanCounts };

export type DeployRun = {
  id: number;
  runNumber: number;
  status: string;
  conclusion: string | null;
  title: string;
  event: string;
  actor: string | null;
  headSha: string;
  createdAt: string;
  runStartedAt: string | null;
  updatedAt: string;
  url: string;
};

export type DeployCommit = { sha: string; message: string; author: string | null; date: string | null; url: string; deployed: boolean; pending: boolean };

export type DeployStatus = {
  configured: boolean;
  repository: string;
  workflow: string;
  branch: string;
  serverSha: string | null;
  builtAt: string | null;
  startedAt: string;
  runs: DeployRun[];
  commits: DeployCommit[];
  error: string | null;
};

export type DeployStep = { number: number; name: string; status: string; conclusion: string | null; startedAt: string | null; completedAt: string | null };
export type DeployJob = { id: number; name: string; status: string; conclusion: string | null; startedAt: string | null; completedAt: string | null; url: string; steps: DeployStep[] };

/** A query string from the set values. */
export function query(params: Record<string, string | number | boolean | null | undefined>): string {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== null && v !== undefined && v !== "") q.set(k, String(v));
  const s = q.toString();
  return s ? `?${s}` : "";
}

export type EventLevel = "Info" | "Good" | "Warning" | "Error";
export type BotEvent = { id: number; at: string; level: EventLevel; siteId: number | null; siteName: string | null; message: string };

export type Dashboard = {
  autoBuyArmed: boolean;
  dryRun: boolean;
  trackingEnabled: boolean;
  currency: string;
  totalBudget: number;
  spent: number;
  sites: number;
  sitesEnabled: number;
  sitesInStock: number;
  sitesFailing: number;
  checksLastHour: number;
  recentEvents: BotEvent[];
  recentPurchases: Purchase[];
};

export type Settings = {
  autoBuyArmed: boolean;
  dryRun: boolean;
  totalBudget: number;
  buyRetryMinutes: number;
  currency: string;
  trackingEnabled: boolean;
  defaultIntervalSeconds: number;
  jitterPercent: number;
  minSecondsPerHost: number;
  maxConcurrentChecks: number;
  requestTimeoutSeconds: number;
  userAgent: string;
  historyDays: number;
  scanHistoryDays: number;
  checkoutHistoryDays: number;
  stepScreenshots: boolean;
  discordWebhookUrl: string;
  telegramBotToken: string;
  telegramChatId: string;
  webhookUrl: string;
  notifyInStock: boolean;
  notifyPriceDrop: boolean;
  notifyPurchase: boolean;
  notifyErrors: boolean;
  errorThreshold: number;
};

export type Profile = {
  email: string;
  phone: string;
  firstName: string;
  lastName: string;
  address1: string;
  address2: string;
  city: string;
  province: string;
  postalCode: string;
  country: string;
  cardName: string;
  /** Read: the last 4 digits. Written: a new number, or "" to keep the saved one. */
  cardNumber: string;
  cardExpiryMonth: string;
  cardExpiryYear: string;
  /** Read: always "". Written: a new CVV, or "" to keep the saved one. */
  cardCvv: string;
  hasCvv: boolean;
  clearCard: boolean;
};
