/**
 * Talks to the customer portal's half of the control-plane API.
 *
 * Deliberately a separate client from `src/api.ts`, with a separate token in a
 * separate storage key. They are different credentials for different people, and
 * the server refuses each on the other's endpoints — a shared `token` helper
 * would make it possible for one to be sent where the other belongs, which would
 * then read as a mysterious 401 rather than as the mistake it is.
 */
const BASE = import.meta.env.VITE_API_BASE ?? '';

const KEY = 'zuloone.portal.token';

export const token = {
  get: () => localStorage.getItem(KEY),
  set: (value: string | null) =>
    value ? localStorage.setItem(KEY, value) : localStorage.removeItem(KEY),
};

export interface PlanFacts {
  code: string;
  name: string;
  officeUsers: number | null;
  fieldAgents: number | null;
  backupCadence: string | null;
  restoreDays: number | null;
  supportResponse: string | null;
  availability: string | null;
  sandboxIncluded: boolean;
}

/** What the server says this person may do — never decided in the browser. */
export interface Can {
  viewTenant: boolean;
  viewStats: boolean;
  viewLogs: boolean;
  resetUserPassword: boolean;
  restartTenant: boolean;
  stopStartTenant: boolean;
  manageMembers: boolean;
}

export interface Stand {
  id: string;
  slug: string;
  displayName: string;
  status: string;
  health: string;
  url: string;
  plan: PlanFacts;
  role: string;
  demo: boolean;
  expiresAt: string | null;
  can: Can;
  createdAt?: string;
  containerRunning?: boolean;
  lastCheckedAt?: string | null;
}

export interface StandUser {
  name: string;
  email: string | null;
  active: boolean;
  locked: boolean;
}

export interface Member {
  id: string;
  email: string | null;
  displayName: string | null;
  role: string;
  since: string;
  isSelf: boolean;
}

/**
 * The error the SERVER wrote, not one composed here.
 *
 * Every refusal from the portal carries a sentence explaining itself — "this
 * stand is suspended, so it is read-only here" — and those sentences are the
 * whole reason a customer can sort a problem out without writing to support.
 * Replacing them with "Request failed" throws that away.
 */
export class ApiError extends Error {
  // A plain field and an assignment, not a constructor parameter property: the
  // project builds with `erasableSyntaxOnly`, under which the shorthand is a
  // compile error because it emits runtime code rather than erasing.
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const headers = new Headers(init?.headers);
  headers.set('Content-Type', 'application/json');
  const bearer = token.get();
  if (bearer) headers.set('Authorization', `Bearer ${bearer}`);

  const response = await fetch(`${BASE}${path}`, { ...init, headers });

  if (response.status === 401) {
    // The session died — expired, revoked, or the account was locked. Clearing it
    // is what stops every later call repeating the same failure silently.
    token.set(null);
    throw new ApiError(401, 'Your session has ended. Sign in again.');
  }

  const text = await response.text();
  const body = text ? JSON.parse(text) : {};

  if (!response.ok) {
    throw new ApiError(response.status, body.error ?? `Request failed (${response.status}).`);
  }
  return body as T;
}

const post = <T>(path: string, body?: unknown) =>
  call<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) });

export const portal = {
  context: () =>
    call<{ authenticated: boolean; email: string | null; minPasswordLength: number }>(
      '/api/portal/auth/context'),

  register: (body: { email: string; password: string; displayName?: string; locale?: string }) =>
    post<{ sent: boolean; message: string }>('/api/portal/auth/register', body),

  verify: (t: string) => post<{ verified: boolean; claimed: number }>('/api/portal/auth/verify', { token: t }),

  login: (body: { email: string; password: string; totp?: string }) =>
    post<{ token: string; expiresAt: string; email: string; displayName: string | null }>(
      '/api/portal/auth/login', body),

  forgot: (email: string) => post<{ sent: boolean; message: string }>('/api/portal/auth/forgot', { email }),

  /**
   * Asks for the confirmation link again. Its own call rather than a side effect
   * of a failed sign-in: sending from there meant anybody who knew an address
   * could fill that person's mailbox by attempting to sign in.
   */
  resend: (email: string) => post<{ sent: boolean; message: string }>('/api/portal/auth/resend', { email }),

  reset: (t: string, password: string) =>
    post<{ reset: boolean }>('/api/portal/auth/reset', { token: t, password }),

  changePassword: (currentPassword: string, newPassword: string) =>
    post<{ changed: boolean }>('/api/portal/auth/change-password', { currentPassword, newPassword }),

  logout: () => post<{ success: boolean }>('/api/portal/auth/logout'),

  stands: () => call<{ tenants: Stand[] }>('/api/portal/tenants'),

  stand: (id: string) => call<Stand>(`/api/portal/tenants/${id}`),

  users: (id: string) => call<{ users: StandUser[] }>(`/api/portal/tenants/${id}/users`),

  resetUserPassword: (id: string, userName?: string) =>
    post<{ user: string; password: string; note: string }>(
      `/api/portal/tenants/${id}/reset-password`, { userName: userName ?? null }),

  restart: (id: string) => post<Stand>(`/api/portal/tenants/${id}/restart`),
  stop: (id: string) => post<Stand>(`/api/portal/tenants/${id}/stop`),
  start: (id: string) => post<Stand>(`/api/portal/tenants/${id}/start`),

  logs: (id: string, lines = 200) =>
    call<{ logs: string }>(`/api/portal/tenants/${id}/logs?lines=${lines}`),

  members: (id: string) => call<{ members: Member[] }>(`/api/portal/tenants/${id}/members`),
  addMember: (id: string, email: string, role: string) =>
    post<unknown>(`/api/portal/tenants/${id}/members`, { email, role }),
  removeMember: (id: string, membershipId: string) =>
    call<unknown>(`/api/portal/tenants/${id}/members/${membershipId}`, { method: 'DELETE' }),
};
