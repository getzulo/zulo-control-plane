import { useCallback, useState } from 'react';
import { Link } from 'react-router-dom';
import { Alert, Anchor, Badge, Card, Grid, Group, Loader, Progress, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { IconAlertTriangle, IconDatabase, IconServer } from '@tabler/icons-react';
import { api, type Cluster, type FleetHealth, type Job, type Snapshot } from './api';
import { CHECK_COLOR, JOB_COLOR, ago, fmt, fmtBytes, usePoll } from './shared';

function Stat({ label, value, color, hint }: { label: string; value: string | number; color?: string; hint?: string }) {
  return (
    <Card withBorder padding="md">
      <Text size="xs" c="dimmed" tt="uppercase" fw={600}>{label}</Text>
      <Text fz={28} fw={700} c={color} lh={1.2}>{value}</Text>
      {hint && <Text size="xs" c="dimmed">{hint}</Text>}
    </Card>
  );
}

export function OverviewPage() {
  const [fleet, setFleet] = useState<FleetHealth | null>(null);
  const [cluster, setCluster] = useState<Cluster | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [snapshots, setSnapshots] = useState<Snapshot[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const refresh = useCallback(async () => {
    // Settled, not all: the cluster being unreachable must not blank the fleet
    // numbers, which is exactly when somebody needs them.
    const [f, c, j, s] = await Promise.allSettled([
      api.fleetHealth(), api.cluster(), api.jobs({ limit: 12 }), api.snapshots(),
    ]);
    if (f.status === 'fulfilled') setFleet(f.value); else setError(f.reason?.message ?? 'Could not read the fleet');
    if (c.status === 'fulfilled') setCluster(c.value);
    if (j.status === 'fulfilled') setJobs(j.value);
    if (s.status === 'fulfilled') setSnapshots(s.value);
    setLoading(false);
  }, []);

  usePoll(refresh, 10_000);

  if (loading) return <Group justify="center" p="xl"><Loader /></Group>;

  const running = jobs.filter((j) => j.state === 'Running' || j.state === 'Queued');
  const failedJobs = jobs.filter((j) => j.state === 'Failed').slice(0, 3);
  const newest = snapshots[0];
  const badNodes = (cluster?.members ?? []).filter((m) => m.selfCheck !== 'healthy');
  const copies = (fleet?.tenants ?? []).filter((t) => t.restoredFromSlug);

  return (
    <Stack gap="md">
      <div>
        <Title order={3}>Overview</Title>
        <Text size="sm" c="dimmed">Everything that would make you open this panel</Text>
      </div>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />}>{error}</Alert>}

      <SimpleGrid cols={{ base: 2, sm: 4 }}>
        <Stat label="Tenants" value={fleet?.total ?? 0} hint={`${fleet?.active ?? 0} active`} />
        <Stat label="Down" value={fleet?.down ?? 0} color={fleet?.down ? 'red' : undefined}
          hint={fleet?.down ? 'active but not answering' : 'all answering'} />
        <Stat label="Failed" value={fleet?.failed ?? 0} color={fleet?.failed ? 'red' : undefined} />
        <Stat
          label="Newest backup"
          value={newest ? fmtBytes(newest.sizeBytes) : '—'}
          color={!newest ? 'red' : undefined}
          hint={newest ? `${newest.tenantSlug} · ${fmt(newest.createdAt)}` : 'nothing has been snapshotted'}
        />
      </SimpleGrid>

      {/* Things that need a decision, before the tables that merely inform. */}
      {badNodes.length > 0 && (
        <Alert color={badNodes.some((n) => n.selfCheck === 'broken') ? 'red' : 'orange'} icon={<IconAlertTriangle size={16} />}>
          {badNodes.map((n) => (
            <Text key={n.name} size="sm">
              <b>{n.name}</b>: {n.selfCheck === 'stale'
                ? `last reported ${ago(n.selfCheckAgeSeconds)} — it publishes every 5 minutes, so it or the machine stopped`
                : n.selfCheck === 'never' ? 'has never reported' : `self-check says ${n.selfCheck}`}
            </Text>
          ))}
          <Anchor component={Link} to="/infrastructure" size="sm">Open infrastructure →</Anchor>
        </Alert>
      )}

      {copies.length > 0 && (
        <Alert color="grape">
          {copies.length} restored {copies.length === 1 ? 'copy is' : 'copies are'} waiting on a decision, each costing
          a container and a database. <Anchor component={Link} to="/backups" size="sm">Review them →</Anchor>
        </Alert>
      )}

      <Grid>
        <Grid.Col span={{ base: 12, md: 7 }}>
          <Card withBorder padding="md" h="100%">
            <Group justify="space-between" mb="xs">
              <Text fw={600}>Activity</Text>
              <Anchor component={Link} to="/activity" size="xs">All jobs →</Anchor>
            </Group>
            {running.length === 0 && failedJobs.length === 0 ? (
              <Text size="sm" c="dimmed">Nothing running, nothing failed recently.</Text>
            ) : (
              <Stack gap="sm">
                {running.map((j) => (
                  <div key={j.id}>
                    <Group justify="space-between">
                      <Text size="sm"><b>{j.kind}</b>{j.tenantSlug ? ` · ${j.tenantSlug}` : ''}</Text>
                      <Text size="xs" c="dimmed">{j.step}</Text>
                    </Group>
                    <Progress value={j.progress} size="sm" animated mt={4} />
                  </div>
                ))}
                {failedJobs.map((j) => (
                  <Alert key={j.id} color="red" p="xs">
                    <Text size="xs"><b>{j.kind}</b>{j.tenantSlug ? ` · ${j.tenantSlug}` : ''} — {j.error}</Text>
                  </Alert>
                ))}
              </Stack>
            )}
          </Card>
        </Grid.Col>

        <Grid.Col span={{ base: 12, md: 5 }}>
          <Card withBorder padding="md" h="100%">
            <Group justify="space-between" mb="xs">
              <Text fw={600}>Cluster</Text>
              <Anchor component={Link} to="/infrastructure" size="xs">Details →</Anchor>
            </Group>
            {!cluster?.reachable ? (
              <Group gap="xs"><IconAlertTriangle size={16} color="red" /><Text size="sm" c="red">No node answered</Text></Group>
            ) : (
              <Stack gap="xs">
                {cluster.members.map((m) => (
                  <Group key={m.name} justify="space-between">
                    <Group gap={6}>
                      {m.role.toLowerCase() === 'leader' ? <IconServer size={14} /> : <IconDatabase size={14} />}
                      <Text size="sm">{m.name}</Text>
                      <Badge size="xs" variant="light" color={m.role.toLowerCase() === 'leader' ? 'grape' : 'blue'}>{m.role}</Badge>
                    </Group>
                    <Badge size="xs" variant="light" color={CHECK_COLOR[m.selfCheck] ?? 'gray'}>{m.selfCheck}</Badge>
                  </Group>
                ))}
              </Stack>
            )}
          </Card>
        </Grid.Col>
      </Grid>
    </Stack>
  );
}

/** Every job the panel has ever run. The audit trail, and where a failure is read. */
export function ActivityPage() {
  const [jobs, setJobs] = useState<Job[]>([]);
  const [open, setOpen] = useState<Job | null>(null);
  const [loading, setLoading] = useState(true);

  usePoll(async () => {
    try { setJobs(await api.jobs({ limit: 100 })); } catch { /* transient */ }
    finally { setLoading(false); }
  }, 8000);

  return (
    <Stack gap="md">
      <div>
        <Title order={3}>Activity</Title>
        <Text size="sm" c="dimmed">Everything the panel has done, and what it is doing now</Text>
      </div>

      {loading ? <Group justify="center" p="xl"><Loader /></Group> : (
        <Card withBorder padding={0}>
          <Stack gap={0}>
            {jobs.map((j) => (
              <Group
                key={j.id} justify="space-between" p="sm" wrap="nowrap"
                style={{ borderBottom: '1px solid var(--mantine-color-default-border)', cursor: 'pointer' }}
                onClick={() => void api.job(j.id).then(setOpen)}
              >
                <Group gap="sm" wrap="nowrap" style={{ minWidth: 0 }}>
                  <Badge color={JOB_COLOR[j.state] ?? 'gray'} variant="light" w={92}>{j.state}</Badge>
                  <div style={{ minWidth: 0 }}>
                    <Text size="sm" fw={500}>
                      {j.kind}{j.tenantSlug ? <Text span c="dimmed"> · {j.tenantSlug}</Text> : null}
                    </Text>
                    <Text size="xs" c={j.error ? 'red' : 'dimmed'} lineClamp={1}>{j.error ?? j.step ?? ''}</Text>
                  </div>
                </Group>
                <div style={{ textAlign: 'right', flex: 'none' }}>
                  <Text size="xs">{fmt(j.startedAt ?? j.createdAt)}</Text>
                  <Text size="xs" c="dimmed">{j.createdBy ?? '—'}</Text>
                </div>
              </Group>
            ))}
            {jobs.length === 0 && <Text ta="center" c="dimmed" py="xl">Nothing yet</Text>}
          </Stack>
        </Card>
      )}

      {open && (
        <Card withBorder padding="md">
          <Group justify="space-between" mb="xs">
            <Text fw={600}>{open.kind}{open.tenantSlug ? ` · ${open.tenantSlug}` : ''}</Text>
            <Anchor size="xs" onClick={() => setOpen(null)}>Close</Anchor>
          </Group>
          <Text size="xs" c="dimmed" mb="xs">
            {fmt(open.startedAt ?? open.createdAt)} → {fmt(open.finishedAt)} · {open.createdBy ?? 'unknown'}
          </Text>
          <pre style={{
            fontSize: 11, whiteSpace: 'pre-wrap', margin: 0, maxHeight: '50vh', overflow: 'auto',
            background: 'var(--mantine-color-default-hover)', padding: 12, borderRadius: 4,
          }}>{open.log ?? '(no log)'}</pre>
        </Card>
      )}
    </Stack>
  );
}
