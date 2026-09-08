/** Talks to the control-plane API. The base URL is same-origin by default so the
 *  dashboard can be served by the control plane itself; VITE_API_BASE overrides it
 *  while developing against a separately-run backend. */
const BASE = import.meta.env.VITE_API_BASE ?? '';

export type TenantStatus = 'Provisioning' | 'Active' | 'Suspended' | 'Failed' | 'Deleting';
export type TenantHealth = 'Unknown' | 'Ok' | 'Down';

export interface Tenant {
  id: string;
  slug: string;
  displayName?: string | null;
  status: TenantStatus;
  health: TenantHealth;
  imageTag: string;
  adminEmail?: string | null;
  plan?: string | null;
  databaseName?: string | null;
  containerId?: string | null;
  lastHealthAt?: string | null;
  /** Why provisioning or the last action failed; null when fine. */
  lastError?: string | null;
  /** True when a one-time administrator password is waiting to be read. */
  hasUnreadAdminPassword?: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface FleetHealth {
  total: number;
  active: number;
  suspended: number;
  failed: number;
  /** Active tenants that failed their last probe. */
  down: number;
  tenants: Tenant[];
}

export interface AuthContext {
  authenticated: boolean;
  email: string | null;
  /** How this request was authenticated, as the SERVER sees it. */
  mode: 'access' | 'local' | 'anonymous';
  /** True only when this request arrived on the break-glass listener. */
  localLoginAvailable: boolean;
  enrolled: boolean;
}

const TOKEN_KEY = 'zuloone.cp.token';

export const token = {
  get: () => localStorage.getItem(TOKEN_KEY),
  set: (value: string | null) =>
    value ? localStorage.setItem(TOKEN_KEY, value) : localStorage.removeItem(TOKEN_KEY),
};

/** Set by the app once it knows how it got in; decides what a 401 means. */
let currentMode: AuthContext['mode'] = 'anonymous';
export function setMode(mode: AuthContext['mode']) { currentMode = mode; }

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const bearer = token.get();
  const response = await fetch(`${BASE}${path}`, {
    ...init,
    // same-origin so the CF_Authorization cookie rides along on the initial
    // navigation; the API itself only ever reads the Access HEADER.
    credentials: 'same-origin',
    headers: {
      'Content-Type': 'application/json',
      // Nothing to send behind Access — Cloudflare attaches the assertion itself.
      ...(bearer ? { Authorization: `Bearer ${bearer}` } : {}),
      ...(init?.headers ?? {}),
    },
  });

  if (response.status === 401) {
    // Branching on mode is the whole point. Behind Access a 401 means the
    // Cloudflare session expired, and a reload lets the edge re-issue it;
    // redirecting to a local login form there would strand the operator on a page
    // that cannot help them. In break-glass mode the token is simply stale.
    if (currentMode === 'access') { location.reload(); }
    else { token.set(null); }
  }

  if (!response.ok) {
    // The API answers failures with { error }, so surface that rather than a bare status.
    const body = await response.text();
    let message = body;
    try { message = JSON.parse(body).error ?? body; } catch { /* not JSON */ }
    throw new Error(message || `${response.status} ${response.statusText}`);
  }
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

export const api = {
  /** ASKED on every boot, never inferred. See auth.tsx. */
  context: () => request<AuthContext>('/api/auth/context'),

  login: (email: string, password: string, totp: string) =>
    request<{ token: string; expiresAt: string; email: string }>(
      '/api/auth/login', { method: 'POST', body: JSON.stringify({ email, password, totp }) }),

  logout: () => request<{ success: boolean }>('/api/auth/logout', { method: 'POST' }),

  enrolBegin: (email: string, password: string) =>
    request<{ secret: string; uri: string }>(
      '/api/auth/enrol/begin', { method: 'POST', body: JSON.stringify({ email, password }) }),

  enrolConfirm: (email: string, password: string, secret: string, totp: string) =>
    request<{ enrolled: boolean }>(
      '/api/auth/enrol/confirm', { method: 'POST', body: JSON.stringify({ email, password, secret, totp }) }),

  fleetHealth: () => request<FleetHealth>('/api/fleet/health'),
  listTenants: () => request<Tenant[]>('/api/tenants'),

  /** Returns 202 immediately; watch the row's status for the rest. */
  createTenant: (body: { slug: string; displayName?: string; adminEmail: string; imageTag?: string; plan?: string }) =>
    request<Tenant>('/api/tenants', { method: 'POST', body: JSON.stringify(body) }),

  stop: (id: string) => request<Tenant>(`/api/tenants/${id}/stop`, { method: 'POST' }),
  start: (id: string) => request<Tenant>(`/api/tenants/${id}/start`, { method: 'POST' }),
  restart: (id: string) => request<Tenant>(`/api/tenants/${id}/restart`, { method: 'POST' }),
  logs: (id: string, lines = 200) => request<{ logs: string }>(`/api/tenants/${id}/logs?lines=${lines}`),

  /** Shown once, then gone. Only ever set when the invitation could not be sent. */
  adminPassword: (id: string) =>
    request<{ user: string; password: string; note: string }>(
      `/api/tenants/${id}/admin-password`, { method: 'POST' }),

  /** The slug must be repeated — this destroys the tenant's data and there is no backup yet. */
  remove: (id: string, confirmSlug: string) =>
    request<{ success: boolean }>(`/api/tenants/${id}?confirmSlug=${encodeURIComponent(confirmSlug)}`, { method: 'DELETE' }),
};
