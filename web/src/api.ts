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

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${BASE}${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
  });
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
  fleetHealth: () => request<FleetHealth>('/api/fleet/health'),
  listTenants: () => request<Tenant[]>('/api/tenants'),

  /** Provisioning runs inline and first boot takes minutes — expect a long wait. */
  createTenant: (body: { slug: string; displayName?: string; adminEmail: string; imageTag?: string; plan?: string }) =>
    request<Tenant>('/api/tenants', { method: 'POST', body: JSON.stringify(body) }),

  stop: (id: string) => request<Tenant>(`/api/tenants/${id}/stop`, { method: 'POST' }),
  start: (id: string) => request<Tenant>(`/api/tenants/${id}/start`, { method: 'POST' }),
  restart: (id: string) => request<Tenant>(`/api/tenants/${id}/restart`, { method: 'POST' }),
  logs: (id: string, lines = 200) => request<{ logs: string }>(`/api/tenants/${id}/logs?lines=${lines}`),

  /** The slug must be repeated — this destroys the tenant's data and there is no backup yet. */
  remove: (id: string, confirmSlug: string) =>
    request<{ success: boolean }>(`/api/tenants/${id}?confirmSlug=${encodeURIComponent(confirmSlug)}`, { method: 'DELETE' }),
};
