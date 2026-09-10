/** Talks to the control-plane API. The base URL is same-origin by default so the
 *  dashboard can be served by the control plane itself; VITE_API_BASE overrides it
 *  while developing against a separately-run backend. */
const BASE = import.meta.env.VITE_API_BASE ?? '';

export type TenantStatus = 'Provisioning' | 'Active' | 'Suspended' | 'Failed' | 'Deleting';
export type TenantHealth = 'Unknown' | 'Ok' | 'Down';

/**
 * What the running panel process says it is. Read from the server rather than
 * baked into the bundle: a cached bundle can outlive the binary that served it,
 * and the two would then disagree exactly when someone needs to know which code
 * they were looking at.
 */
export interface Health {
  status: string;
  version: string;
  /** Short git sha, or empty for a build made without one. */
  build: string;
  startedUtc: string;
}

/**
 * One knob, as declared in SettingsCatalog on the server. The screen renders
 * itself from these, so adding a setting is a server-side entry and no UI change.
 */
export interface SettingDef {
  key: string;
  label: string;
  description: string;
  kind: 'Int' | 'Bytes' | 'Seconds' | 'Hours' | 'Days' | 'Bool' | 'Text' | 'TextList' | 'Image';
  default: string;
  /** False means the process read it at startup; a change needs the container recreated. */
  runtimeEditable: boolean;
  min?: number | null;
  max?: number | null;
  /** The value in force, after database over config over default. */
  effective: string;
  /** The override, when one exists. */
  dbValue?: string | null;
  /** What cp.env says — what reverting would land on. */
  configValue?: string | null;
  source: 'Database' | 'Config' | 'Default';
}

export interface SettingGroup {
  group: string;
  settings: SettingDef[];
}

export interface Tenant {
  id: string;
  slug: string;
  displayName?: string | null;
  status: TenantStatus;
  health: TenantHealth;
  /** Who created the database and container — decides whether they may be destroyed. */
  origin: 'Provisioned' | 'Adopted';
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
  /** Set on a throwaway copy produced by a restore — never on a real tenant. */
  restoredFromSlug?: string | null;
  restoredAt?: string | null;
  /** The database set aside by a swap; the only way back to the data from before it. */
  previousDatabaseName?: string | null;
  previousDatabaseAt?: string | null;
  createdAt: string;
  updatedAt: string;
}

export type JobState = 'Queued' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled';
export type JobKind =
  | 'Provision' | 'Adopt' | 'Snapshot' | 'Restore' | 'Swap' | 'Upgrade' | 'Backup' | 'Switchover'
  /** Retention sweep. Runs through the queue so it cannot race a dump or a restore. */
  | 'Prune'
  /** Dump of the panel's OWN database — the record of which container belongs to whom. */
  | 'RegistrySnapshot';

export interface Job {
  id: string;
  kind: JobKind;
  state: JobState;
  tenantId?: string | null;
  tenantSlug?: string | null;
  step?: string | null;
  progress: number;
  error?: string | null;
  createdBy?: string | null;
  createdAt: string;
  startedAt?: string | null;
  finishedAt?: string | null;
  /** Only on the single-job endpoint — the list omits it deliberately. */
  log?: string | null;
}

export interface ClusterMember {
  name: string;
  role: string;
  state: string;
  host?: string | null;
  port: number;
  timeline?: number | null;
  lag?: number | null;
  lsn?: string | null;
  /** The node's own five-minute check: healthy | degraded | broken | stale | never. */
  selfCheck: string;
  selfCheckAt?: string | null;
  selfCheckAgeSeconds?: number | null;
  selfCheckReport?: string | null;
}

export interface Cluster {
  configured: boolean;
  /** False when no node answered — distinct from an empty member list. */
  reachable: boolean;
  scope?: string | null;
  members: ClusterMember[];
  unmatchedReports: { node: string; status: string; receivedAt: string; report?: string | null }[];
  /** The CLUSTER's pgBackRest inventory, as distinct from per-tenant dumps. */
  backups?: ClusterBackups | null;
}

/**
 * pgBackRest, reported by whichever node holds the repository. Only as fresh as
 * that node's last five-minute run — `asOfStale` says when to stop believing it.
 */
export interface ClusterBackups {
  node: string;
  stanza?: string | null;
  status?: string | null;
  count: number;
  newest?: { label: string; type: string; stopped: string; sizeBytes: number; ageHours: number } | null;
  lastFull?: string | null;
  backups: { label: string; type: string; stopped: string; sizeBytes: number }[];
  asOf: string;
  asOfStale: boolean;
}

/** What one tenant is using right now, container and database. */
export interface TenantStats {
  container?: {
    cpuPercent: number;
    memoryBytes: number;
    memoryLimitBytes: number;
    memoryPercent: number;
    startedAt?: string | null;
    restartCount: number;
    state: string;
    networkRxBytes: number;
    networkTxBytes: number;
  } | null;
  database?: {
    sizeBytes: number;
    connections: number;
    maxConnections: number;
    tableCount: number;
    largestTableBytes?: number | null;
    largestTableName?: string | null;
  } | null;
  /** Set when one half could not be read; the other half is still returned. */
  error?: string | null;
}

/** The business layer a tenant HAS, beside what its image carries. */
export interface TenantModels {
  /** Set when the tenant's database could not be read; the rest is still returned. */
  error?: string | null;
  imageTag: string;
  /** Which platform build the distribution was cut from, from the image labels. */
  platformBuild?: string | null;
  workspaceCommit?: string | null;
  /** False for a platform-only image — which is itself why a tenant has no models. */
  carriesPackages: boolean;
  models: {
    name: string;
    version?: string | null;
    publisher?: string | null;
    isSystem: boolean;
    isEnabled: boolean;
    compilationStatus?: string | null;
    compilationError?: string | null;
    /** What the image offers for this model, when it offers one. */
    offers?: string | null;
    behind: boolean;
  }[];
  /** In the image and not in the database — excluded by the allow-list, or a failed install. */
  notInstalled: { name: string; version: string }[];
}

export interface ModelCatalogue {
  /** Set when the registry could not be reached; the rest is still shaped. */
  error?: string | null;
  registry?: string | null;
  /** Every model any image declares, with the versions available and where each lives. */
  models: {
    model: string;
    versions: { version: string; images: string[] }[];
  }[];
  /** One row per image that declares a model set. Images without one are the platform. */
  images: {
    image: string;
    repository: string;
    tag: string;
    platform?: string | null;
    workspace?: string | null;
    models: { name: string; version: string }[];
  }[];
  tenants: {
    id: string;
    slug: string;
    imageTag: string;
    status: string;
    error?: string | null;
    /** What the tenant's image declares. Null means its labels could not be read. */
    carries?: { name: string; version: string }[] | null;
    installed: {
      name: string;
      version?: string | null;
      isSystem: boolean;
      isEnabled: boolean;
      compilationStatus?: string | null;
      compilationError?: string | null;
      offers?: string | null;
    }[];
  }[];
}

export interface SnapshotList {
  snapshots: Snapshot[];
  /** Every row, not just the ones returned — the list is capped at 200. */
  totalCount: number;
  truncated: boolean;
  disk: {
    freeBytes: number;
    totalBytes: number;
    /** Summed over EVERY row in the database, not the page above. */
    usedBySnapshots: number;
    minFreeBytes: number;
    /** Below this the server refuses to start a snapshot at all. */
    belowFloor: boolean;
  };
}

export interface Snapshot {
  id: string;
  tenantId?: string | null;
  tenantSlug: string;
  databaseName: string;
  sizeBytes: number;
  kind: 'Manual' | 'PreUpgrade' | 'PreSwap' | 'Registry';
  note?: string | null;
  imageTag?: string | null;
  createdAt: string;
  /** Whether the dump file is still where the row says it is. */
  onDisk: boolean;
}

export interface ImageTag {
  tag: string;
  image: string;
  inUseBy: string[];
  isDefault: boolean;
  /** Manifest digest; null when the registry would not say. */
  digest?: string | null;
  /** Other tags on the SAME manifest. Deleting this tag deletes them too. */
  alsoTagged: string[];
  canDelete: boolean;
  /** Why not, when canDelete is false — shown on hover instead of a dead button. */
  deleteBlockedBy?: string | null;
}

export interface Images {
  registry?: string | null;
  repository?: string;
  defaultImage?: string;
  releases: ImageTag[];
  builds: ImageTag[];
  error?: string;
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
  /**
   * What this panel binary is. Anonymous, so the stamp renders on the login
   * screen too — the version matters most to somebody who cannot get in.
   */
  health: () => request<Health>('/health'),

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

  /** Returns 202 with the tenant AND the job building it. */
  createTenant: (body: { slug: string; displayName?: string; adminEmail: string; imageTag?: string; plan?: string }) =>
    request<{ tenant: Tenant; jobId: string }>('/api/tenants', { method: 'POST', body: JSON.stringify(body) }),

  /** Brings a hand-deployed tenant under management. Its users are logged out. */
  adoptTenant: (body: { slug: string; displayName?: string; adminEmail?: string; containerName?: string }) =>
    request<{ tenant: Tenant; jobId: string }>('/api/tenants/adopt', { method: 'POST', body: JSON.stringify(body) }),

  tenant: (id: string) => request<Tenant>(`/api/tenants/${id}`),

  stop: (id: string) => request<Tenant>(`/api/tenants/${id}/stop`, { method: 'POST' }),
  start: (id: string) => request<Tenant>(`/api/tenants/${id}/start`, { method: 'POST' }),
  restart: (id: string) => request<Tenant>(`/api/tenants/${id}/restart`, { method: 'POST' }),
  logs: (id: string, lines = 200) => request<{ logs: string }>(`/api/tenants/${id}/logs?lines=${lines}`),

  /** Container CPU/memory and database size/connections. Takes ~1s: a real CPU
   *  percentage needs two samples from the Docker stats stream. */
  models: (id: string) => request<TenantModels>(`/api/tenants/${id}/models`),

  stats: (id: string) => request<TenantStats>(`/api/tenants/${id}/stats`),

  /** What the container REPORTS, as opposed to the tag pinned in the registry. */
  running: (id: string) => request<{
    reachable: boolean; version?: string | null; build?: string | null;
    startedUtc?: string | null; pinnedImage: string; matchesPinned?: boolean; error?: string;
  }>(`/api/tenants/${id}/running`),

  tenantUsers: (id: string) =>
    request<{ name: string; email?: string | null; active: boolean; locked: boolean }[]>(
      `/api/tenants/${id}/users`),

  /** Sets a new password and returns it ONCE. Written straight into the tenant's
   *  database — the case this exists for is "nobody can sign in". */
  resetPassword: (id: string, confirmSlug: string, userName?: string) =>
    request<{ user: string; password: string; url: string; note: string }>(
      `/api/tenants/${id}/reset-password`, {
        method: 'POST', body: JSON.stringify({ userName: userName ?? null, confirmSlug }),
      }),

  /** Shown once, then gone. Only ever set when the invitation could not be sent. */
  adminPassword: (id: string) =>
    request<{ user: string; password: string; note: string }>(
      `/api/tenants/${id}/admin-password`, { method: 'POST' }),

  /** DESTROYS the tenant: container, database, role. The slug must be repeated. */
  remove: (id: string, confirmSlug: string) =>
    request<{ success: boolean }>(`/api/tenants/${id}?confirmSlug=${encodeURIComponent(confirmSlug)}`, { method: 'DELETE' }),

  /** Stops MANAGING the tenant. The container keeps running and the data stays. */
  release: (id: string, confirmSlug: string) =>
    request<{ success: boolean; released: string; note: string }>(
      `/api/tenants/${id}/release?confirmSlug=${encodeURIComponent(confirmSlug)}`, { method: 'POST' }),

  // ------------------------------------------------------------------ jobs ---
  jobs: (params?: { tenantId?: string; active?: boolean; limit?: number }) => {
    const q = new URLSearchParams();
    if (params?.tenantId) q.set('tenantId', params.tenantId);
    if (params?.active) q.set('active', 'true');
    if (params?.limit) q.set('limit', String(params.limit));
    const s = q.toString();
    return request<Job[]>(`/api/jobs${s ? `?${s}` : ''}`);
  },
  job: (id: string) => request<Job>(`/api/jobs/${id}`),

  // ---------------------------------------------------------- infrastructure ---
  cluster: () => request<Cluster>('/api/infra/cluster'),

  /** Drops every open connection to the current leader. The node name is retyped. */
  switchover: (candidate: string, confirmNode: string) =>
    request<{ success: boolean; from: string; to: string; detail: string }>(
      '/api/infra/switchover', { method: 'POST', body: JSON.stringify({ candidate, confirmNode }) }),

  // ------------------------------------------------------------- snapshots ---
  snapshots: (tenantId?: string) =>
    request<SnapshotList>(`/api/snapshots${tenantId ? `?tenantId=${tenantId}` : ''}`),

  takeSnapshot: (tenantId: string, note?: string) =>
    request<{ jobId: string }>(`/api/tenants/${tenantId}/snapshot`, {
      method: 'POST', body: JSON.stringify({ note: note ?? null }),
    }),

  /** Builds a THROWAWAY tenant from the dump. Nothing live is touched. */
  restoreSnapshot: (snapshotId: string) =>
    request<{ jobId: string }>(`/api/snapshots/${snapshotId}/restore`, { method: 'POST' }),

  deleteSnapshot: (id: string) => request<{ success: boolean }>(`/api/snapshots/${id}`, { method: 'DELETE' }),

  /** Puts a verified copy's database under the live tenant. */
  swap: (liveTenantId: string, scratchTenantId: string, confirmSlug: string) =>
    request<{ jobId: string }>(`/api/tenants/${liveTenantId}/swap`, {
      method: 'POST', body: JSON.stringify({ scratchTenantId, confirmSlug }),
    }),

  /** Drops the database a swap set aside — the last way back. */
  discardPrevious: (tenantId: string, confirmSlug: string) =>
    request<{ success: boolean; dropped: string }>(
      `/api/tenants/${tenantId}/previous-database?confirmSlug=${encodeURIComponent(confirmSlug)}`, { method: 'DELETE' }),

  // ---------------------------------------------------------------- images ---
  images: () => request<Images>('/api/images'),

  /**
   * Turn a build into a release. Adds a NAME to the existing manifest — no
   * rebuild, so the released bytes are the tested bytes. Pass version null to
   * let the server take the next patch of the current month.
   */
  promoteImage: (tag: string, version: string | null, notes: string | null) =>
    request<{ version: string; image: string; digest: string; promotedFrom: string }>(
      `/api/images/${encodeURIComponent(tag)}/promote`, {
        method: 'POST', body: JSON.stringify({ version, notes }),
      }),

  settings: () => request<{ groups: SettingGroup[] }>('/api/settings'),

  /** All or nothing: the server validates every key before writing any. */
  saveSettings: (writes: { key: string; value: string }[]) =>
    request<{ success: boolean; applied: number }>('/api/settings', {
      method: 'PUT', body: JSON.stringify(writes),
    }),

  /** Drop the override; the key falls back to cp.env and then to the default. */
  revertSetting: (key: string) =>
    request<{ success: boolean; effective: string; source: string }>(
      `/api/settings/${encodeURIComponent(key)}`, { method: 'DELETE' }),

  /**
   * Removes the MANIFEST, so every tag on it goes. The API refuses when any of
   * those names is in use, is a release, or is the fleet default.
   */
  removeImage: (tag: string) =>
    request<{ success: boolean; digest: string; removed: string[]; note: string }>(
      `/api/images/${encodeURIComponent(tag)}?confirmTag=${encodeURIComponent(tag)}`, { method: 'DELETE' }),

  /** Apply the retention policy now rather than at the next scheduled sweep. */
  prune: () => request<{ jobId: string }>('/api/snapshots/prune', { method: 'POST' }),

  /** The registry's model catalogue plus what each tenant actually runs. */
  modelCatalogue: () => request<ModelCatalogue>('/api/models'),

  /** Snapshots first — that snapshot is the only way back past a migration. */
  upgrade: (tenantId: string, imageTag: string) =>
    request<{ jobId: string }>(`/api/tenants/${tenantId}/upgrade`, {
      method: 'POST', body: JSON.stringify({ imageTag }),
    }),
};
