import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { ActionIcon, Badge, Code, CopyButton, Group, Progress, Text, Tooltip, UnstyledButton } from '@mantine/core';
import { IconRefresh } from '@tabler/icons-react';
import { api, type Health, type Job } from './api';

/**
 * React 19 nulls `event.currentTarget` once a sync setState re-renders (and
 * Mantine Switch then reads `.checked` after the consumer onChange). Take the
 * flag now. For Switch, apply the update on a microtask so Mantine finishes first.
 */
export function inputChecked(e: { currentTarget: { checked?: boolean } | null; target: EventTarget | null }): boolean {
  const el = (e.currentTarget ?? e.target) as { checked?: boolean } | null;
  return Boolean(el?.checked);
}

export function afterInputEvent(apply: () => void): void {
  queueMicrotask(apply);
}

/**
 * Which panel build this is — read from the SERVER, never from a compile-time
 * constant baked into the bundle.
 *
 * A constant reports what the JavaScript was built from, and the JavaScript is
 * served by whatever container happens to be running: after an upgrade a cached
 * bundle can outlive the binary that shipped it, so the two disagree exactly
 * when someone is working out which code they were looking at. Asking /health
 * cannot drift, because the answer comes from the process itself.
 *
 * Rendered on the login screen as well as inside the shell — the version matters
 * most to somebody who cannot get in, and that is the one moment the panel's own
 * navigation is unavailable.
 *
 * Fetched once on mount. It cannot change without a page load, since a new
 * container means a new bundle.
 */
export function BuildStamp() {
  const [health, setHealth] = useState<Health | null>(null);
  useEffect(() => { api.health().then(setHealth).catch(() => setHealth(null)); }, []);

  if (!health) return <Text size="xs" c="dimmed">panel — version unavailable</Text>;

  // The whole line, ready to paste into a bug report.
  const full = `panel ${health.version}${health.build ? ` (${health.build})` : ''}`;

  return (
    <CopyButton value={full}>
      {({ copied, copy }) => (
        <Tooltip label={copied ? 'Copied' : 'Click to copy for a bug report'} withArrow>
          <UnstyledButton onClick={copy} style={{ overflow: 'hidden', width: '100%' }}>
            <Group gap={4} wrap="nowrap">
              <Text size="xs" c="dimmed" truncate>
                panel <Code fz={10}>{health.version}</Code>
                {health.build && <> · <Code fz={10}>{health.build}</Code></>}
              </Text>
            </Group>
          </UnstyledButton>
        </Tooltip>
      )}
    </CopyButton>
  );
}

/** Absolute time, because an ops panel is read alongside logs and journals. */
export const fmt = (value?: string | null) => (value ? new Date(value).toLocaleString() : '—');

export const fmtBytes = (n?: number | null) => {
  if (n == null) return '—';
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(0)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
};

/** "3 min ago" for freshness, where the exact instant matters less than the gap. */
export function ago(seconds?: number | null): string {
  if (seconds == null) return '—';
  if (seconds < 90) return `${Math.round(seconds)} s ago`;
  if (seconds < 5400) return `${Math.round(seconds / 60)} min ago`;
  return `${(seconds / 3600).toFixed(1)} h ago`;
}

export const STATUS_COLOR: Record<string, string> = {
  Active: 'green', Provisioning: 'blue', Suspended: 'gray', Failed: 'red', Deleting: 'orange',
};

export const JOB_COLOR: Record<string, string> = {
  Queued: 'gray', Running: 'blue', Succeeded: 'green', Failed: 'red', Cancelled: 'orange',
};

/** Node self-check verdict. `stale` is its own colour: silence is not health. */
export const CHECK_COLOR: Record<string, string> = {
  healthy: 'green', degraded: 'yellow', broken: 'red', stale: 'orange', never: 'gray',
};

export const ROLE_COLOR: Record<string, string> = {
  postgres: 'blue', etcd: 'violet', mongo: 'green', app: 'teal', panel: 'grape', ci: 'gray', host: 'dark',
};

export const ROLE_LABEL: Record<string, string> = {
  postgres: 'Postgres', etcd: 'etcd', mongo: 'Mongo', app: 'App', panel: 'Panel', ci: 'CI', host: 'Host',
};

export const ROLE_ORDER = ['postgres', 'etcd', 'mongo', 'app', 'panel', 'ci', 'host'];

type RefreshFn = () => void | Promise<void>;

const RefreshCtx = createContext<{
  register: (fn: RefreshFn) => () => void;
  refresh: () => Promise<void>;
  busy: boolean;
}>({
  register: () => () => {},
  refresh: async () => {},
  busy: false,
});

/**
 * One refresh for the whole shell. Pages register via {@link usePoll}; the
 * header button calls them. A full reload is the wrong tool — it drops drafts,
 * scrolls and open drawers, and the data is already fetched in pieces.
 */
export function RefreshProvider({ children }: { children: ReactNode }) {
  const subs = useRef(new Set<RefreshFn>());
  const [busy, setBusy] = useState(false);

  const register = useCallback((fn: RefreshFn) => {
    subs.current.add(fn);
    return () => { subs.current.delete(fn); };
  }, []);

  const refresh = useCallback(async () => {
    setBusy(true);
    try {
      await Promise.allSettled([...subs.current].map((fn) => Promise.resolve(fn())));
    } finally {
      setBusy(false);
    }
  }, []);

  return <RefreshCtx.Provider value={{ register, refresh, busy }}>{children}</RefreshCtx.Provider>;
}

/** Header control: refetch whatever the open page is watching, no reload. */
export function RefreshButton() {
  const { refresh, busy } = useContext(RefreshCtx);
  return (
    <Tooltip label="Refresh this screen — does not reload the panel">
      <ActionIcon
        variant="subtle" color="gray" loading={busy}
        onClick={() => { void refresh(); }}
        aria-label="Refresh this screen"
      >
        <IconRefresh size={17} />
      </ActionIcon>
    </Tooltip>
  );
}

/**
 * Polls while the tab is visible, and runs again when the header Refresh
 * button is clicked.
 *
 * An operations panel is left open for hours on a second monitor, and a plain
 * setInterval keeps hitting the API from a tab nobody is looking at. Pausing on
 * hidden and refreshing immediately on return costs one listener and means the
 * numbers are current the moment the tab is looked at again.
 *
 * <c>ms &lt;= 0</c> means mount + manual only — Settings, where a background
 * poll would wipe a draft the operator is still typing.
 */
export function usePoll(fn: () => void | Promise<void>, ms = 10_000) {
  const saved = useRef(fn);
  const inflight = useRef(false);
  const { register } = useContext(RefreshCtx);
  // Updated in an EFFECT, not during render. Writing to a ref while rendering is
  // a side effect in the render phase, which React is free to run twice or discard.
  useEffect(() => { saved.current = fn; });

  const run = useCallback(() => {
    if (inflight.current) return Promise.resolve();
    inflight.current = true;
    return Promise.resolve(saved.current()).finally(() => { inflight.current = false; });
  }, []);

  useEffect(() => register(run), [register, run]);

  useEffect(() => {
    let timer: number | undefined;
    const tick = () => { if (!document.hidden) run(); };
    const start = () => {
      stop();
      if (ms > 0) timer = window.setInterval(tick, ms);
    };
    const stop = () => { if (timer) window.clearInterval(timer); timer = undefined; };

    const onVisibility = () => {
      if (document.hidden) stop();
      else { run(); start(); }
    };

    run();
    start();
    document.addEventListener('visibilitychange', onVisibility);
    return () => { stop(); document.removeEventListener('visibilitychange', onVisibility); };
  }, [ms, run]);
}

/**
 * Follows one job to its end.
 *
 * Every long action in the panel answers with a job id rather than a result, so
 * this is what turns "202 Accepted" into something an operator can watch.
 */
export function useJob(jobId: string | null) {
  const [job, setJob] = useState<Job | null>(null);

  const refresh = useCallback(async () => {
    if (!jobId) return;
    try { setJob(await api.job(jobId)); } catch { /* transient; the next tick retries */ }
  }, [jobId]);

  useEffect(() => { setJob(null); void refresh(); }, [jobId, refresh]);

  useEffect(() => {
    if (!jobId) return;
    // Stop as soon as it is terminal: a finished job never changes again, and
    // polling it forever is the commonest way an ops panel becomes the noisiest
    // client of its own API.
    if (job && (job.state === 'Succeeded' || job.state === 'Failed' || job.state === 'Cancelled')) return;
    const t = window.setInterval(() => void refresh(), 2500);
    return () => window.clearInterval(t);
  }, [jobId, job, refresh]);

  return job;
}

/** A job's live state — the same block wherever one is being watched. */
export function JobProgress({ job }: { job: Job | null }) {
  if (!job) return <Text size="sm" c="dimmed">Starting…</Text>;
  const done = job.state === 'Succeeded';
  const failed = job.state === 'Failed' || job.state === 'Cancelled';

  return (
    <div>
      <Text size="sm" fw={500}>
        <Badge size="sm" color={JOB_COLOR[job.state] ?? 'gray'} variant="light" mr={6}>{job.state}</Badge>
        {job.step ?? job.kind}
      </Text>
      <Progress
        value={job.progress} mt={6} size="sm"
        color={failed ? 'red' : done ? 'green' : 'blue'}
        animated={!done && !failed}
      />
      {job.error && (
        <Code block mt={8} style={{ fontSize: 11, whiteSpace: 'pre-wrap' }} color="red">{job.error}</Code>
      )}
    </div>
  );
}
