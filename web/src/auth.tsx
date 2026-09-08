import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { api, setMode, token, type AuthContext as Ctx } from './api';

interface AuthState {
  ctx: Ctx | null;
  loading: boolean;
  /** Re-asks the server. Call after signing in or out. */
  refresh: () => Promise<void>;
  signOut: () => Promise<void>;
}

const AuthCtx = createContext<AuthState>({
  ctx: null, loading: true, refresh: async () => {}, signOut: async () => {},
});

export const useAuth = () => useContext(AuthCtx);

/**
 * Works out how this browser got in — by ASKING the server, every boot.
 *
 * The rule that keeps break-glass usable: the dashboard must never infer its mode
 * from the URL or from a build-time flag. Behind Cloudflare Access the browser
 * carries an assertion it cannot see; over an SSH tunnel there is none and a
 * password form is needed instead. Guess wrong in the second case and the panel
 * renders, calls the API, gets 401, redirects to a login route that does not
 * exist, serves the SPA fallback and loops — discovered over a tunnel, during
 * whatever outage sent you there.
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  const [ctx, setCtx] = useState<Ctx | null>(null);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    try {
      const next = await api.context();
      setMode(next.mode);
      setCtx(next);
    } catch {
      // Even the context call failing is information: treat it as anonymous and
      // let the UI say so, rather than rendering a dashboard that cannot load.
      setMode('anonymous');
      setCtx({ authenticated: false, email: null, mode: 'anonymous', localLoginAvailable: false, enrolled: false });
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  const signOut = useCallback(async () => {
    // Behind Access there is no local session to end — signing out means ending
    // the Cloudflare one, which only Cloudflare can do.
    if (ctx?.mode === 'local') {
      try { await api.logout(); } catch { /* the token may already be gone */ }
      token.set(null);
    }
    await refresh();
  }, [ctx?.mode, refresh]);

  return <AuthCtx.Provider value={{ ctx, loading, refresh, signOut }}>{children}</AuthCtx.Provider>;
}
