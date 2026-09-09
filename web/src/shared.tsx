import { useCallback, useEffect, useRef, useState } from 'react';
import { Badge, Code, Progress, Text } from '@mantine/core';
import { api, type Job } from './api';

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

/**
 * Polls while the tab is visible.
 *
 * An operations panel is left open for hours on a second monitor, and a plain
 * setInterval keeps hitting the API from a tab nobody is looking at. Pausing on
 * hidden and refreshing immediately on return costs one listener and means the
 * numbers are current the moment the tab is looked at again.
 */
export function usePoll(fn: () => void | Promise<void>, ms = 10_000) {
  const saved = useRef(fn);
  // Updated in an EFFECT, not during render. Writing to a ref while rendering is
  // a side effect in the render phase, which React is free to run twice or discard.
  useEffect(() => { saved.current = fn; });
  useEffect(() => {
    let timer: number | undefined;
    const tick = () => { if (!document.hidden) void saved.current(); };
    const start = () => { stop(); timer = window.setInterval(tick, ms); };
    const stop = () => { if (timer) window.clearInterval(timer); timer = undefined; };

    const onVisibility = () => {
      if (document.hidden) stop();
      else { void saved.current(); start(); }
    };

    void saved.current();
    start();
    document.addEventListener('visibilitychange', onVisibility);
    return () => { stop(); document.removeEventListener('visibilitychange', onVisibility); };
  }, [ms]);
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
