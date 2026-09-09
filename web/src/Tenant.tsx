import { useCallback, useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import {
  Alert, Anchor, Badge, Button, Card, Code, Grid, Group, Loader, Modal, Stack, Table, Tabs, Text, TextInput, Title,
} from '@mantine/core';
import {
  IconAlertTriangle, IconArrowLeft, IconCamera, IconPlayerPlay, IconPlayerStop, IconRotate, IconTrash,
} from '@tabler/icons-react';
import { api, type Job, type Snapshot, type Tenant } from './api';
import { JOB_COLOR, JobProgress, STATUS_COLOR, fmt, fmtBytes, useJob, usePoll } from './shared';

export function TenantPage() {
  const { id = '' } = useParams();
  const [tenant, setTenant] = useState<Tenant | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [snapshots, setSnapshots] = useState<Snapshot[]>([]);
  const [logs, setLogs] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [jobId, setJobId] = useState<string | null>(null);
  const [destroy, setDestroy] = useState<'delete' | 'release' | null>(null);
  const [confirm, setConfirm] = useState('');
  const [revealed, setRevealed] = useState<{ user: string; password: string } | null>(null);
  const job = useJob(jobId);

  const refresh = useCallback(async () => {
    try {
      const [t, j, s] = await Promise.all([api.tenant(id), api.jobs({ tenantId: id, limit: 20 }), api.snapshots(id)]);
      setTenant(t); setJobs(j); setSnapshots(s); setError(null);
    } catch (e) { setError((e as Error).message); }
    finally { setLoading(false); }
  }, [id]);

  usePoll(refresh, 10_000);

  const act = async (fn: () => Promise<unknown>) => {
    try { await fn(); void refresh(); } catch (e) { setError((e as Error).message); }
  };

  const loadLogs = async () => {
    setLogs('Loading…');
    try { setLogs((await api.logs(id, 400)).logs || '(empty)'); }
    catch (e) { setLogs(`Could not read logs: ${(e as Error).message}`); }
  };

  if (loading) return <Group justify="center" p="xl"><Loader /></Group>;
  if (!tenant) return <Alert color="red">{error ?? 'Tenant not found'}</Alert>;

  const isCopy = Boolean(tenant.restoredFromSlug);

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <Group gap="sm">
          <Anchor component={Link} to="/tenants"><IconArrowLeft size={18} /></Anchor>
          <div>
            <Group gap={8}>
              <Title order={3}>{tenant.slug}</Title>
              <Badge color={STATUS_COLOR[tenant.status] ?? 'gray'} variant="light">{tenant.status}</Badge>
              <Badge color={tenant.health === 'Ok' ? 'green' : tenant.health === 'Down' ? 'red' : 'gray'} variant="dot">
                {tenant.health}
              </Badge>
              {isCopy && <Badge color="grape" variant="filled">restored copy</Badge>}
            </Group>
            <Anchor size="sm" href={`https://${tenant.slug}.zulo.one`} target="_blank" rel="noreferrer">
              {tenant.slug}.zulo.one
            </Anchor>
          </div>
        </Group>
        <Group gap="xs">
          {tenant.status === 'Suspended'
            ? <Button size="xs" variant="default" leftSection={<IconPlayerPlay size={14} />} onClick={() => void act(() => api.start(id))}>Start</Button>
            : <Button size="xs" variant="default" leftSection={<IconPlayerStop size={14} />} onClick={() => void act(() => api.stop(id))}>Stop</Button>}
          <Button size="xs" variant="default" leftSection={<IconRotate size={14} />} onClick={() => void act(() => api.restart(id))}>Restart</Button>
          <Button
            size="xs" variant="light" leftSection={<IconCamera size={14} />}
            disabled={!tenant.databaseName}
            onClick={async () => { try { setJobId((await api.takeSnapshot(id)).jobId); } catch (e) { setError((e as Error).message); } }}
          >
            Snapshot
          </Button>
          <Button size="xs" color="red" variant="light" leftSection={<IconTrash size={14} />}
            onClick={() => { setDestroy('delete'); setConfirm(''); }}>
            Delete
          </Button>
        </Group>
      </Group>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />} withCloseButton onClose={() => setError(null)}>{error}</Alert>}
      {tenant.lastError && <Alert color="red" title="Last failure">{tenant.lastError}</Alert>}

      {isCopy && (
        <Alert color="grape">
          A restore of <b>{tenant.restoredFromSlug}</b> taken {fmt(tenant.restoredAt)}. It is disposable: swap it in
          from the Backups screen, or delete it. It costs a container and a database until you do.
        </Alert>
      )}

      {tenant.previousDatabaseName && (
        <Alert color="orange">
          A swap set <Code>{tenant.previousDatabaseName}</Code> aside on {fmt(tenant.previousDatabaseAt)}. That is the
          only way back to the data from before it — discard it from the Backups screen when you are satisfied.
        </Alert>
      )}

      {job && <Card withBorder padding="sm"><JobProgress job={job} /></Card>}

      <Grid>
        <Grid.Col span={{ base: 12, md: 6 }}>
          <Card withBorder padding="md" h="100%">
            <Text fw={600} mb="xs">Deployment</Text>
            <Table withRowBorders={false} verticalSpacing={5} fz="sm">
              <Table.Tbody>
                <Table.Tr><Table.Td c="dimmed">Image</Table.Td><Table.Td><Code>{tenant.imageTag}</Code></Table.Td></Table.Tr>
                <Table.Tr><Table.Td c="dimmed">Container</Table.Td><Table.Td><Code>{tenant.containerId ?? '—'}</Code></Table.Td></Table.Tr>
                <Table.Tr><Table.Td c="dimmed">Database</Table.Td><Table.Td><Code>{tenant.databaseName ?? '—'}</Code></Table.Td></Table.Tr>
                <Table.Tr><Table.Td c="dimmed">Administrator</Table.Td><Table.Td>{tenant.adminEmail ?? '—'}</Table.Td></Table.Tr>
                <Table.Tr><Table.Td c="dimmed">Created</Table.Td><Table.Td>{fmt(tenant.createdAt)}</Table.Td></Table.Tr>
                <Table.Tr><Table.Td c="dimmed">Last probe</Table.Td><Table.Td>{fmt(tenant.lastHealthAt)}</Table.Td></Table.Tr>
              </Table.Tbody>
            </Table>
            {tenant.hasUnreadAdminPassword && (
              <Alert color="blue" mt="sm" p="xs">
                <Text size="xs" mb={4}>
                  The invitation could not be sent, so the one-time password is held here. Reading it erases it.
                </Text>
                <Button size="xs" onClick={async () => {
                  try { const r = await api.adminPassword(id); setRevealed(r); void refresh(); }
                  catch (e) { setError((e as Error).message); }
                }}>Show it once</Button>
              </Alert>
            )}
          </Card>
        </Grid.Col>

        <Grid.Col span={{ base: 12, md: 6 }}>
          <Card withBorder padding="md" h="100%">
            <Text fw={600} mb="xs">Snapshots ({snapshots.length})</Text>
            {snapshots.length === 0 ? (
              <Text size="sm" c="dimmed">None yet. A snapshot is what makes an upgrade reversible.</Text>
            ) : (
              <Stack gap={6}>
                {snapshots.slice(0, 6).map((s) => (
                  <Group key={s.id} justify="space-between">
                    <div>
                      <Text size="sm">{fmt(s.createdAt)}</Text>
                      <Text size="xs" c="dimmed">{s.kind}{s.note ? ` · ${s.note}` : ''}</Text>
                    </div>
                    <Badge variant="light" color={s.onDisk ? 'gray' : 'red'}>
                      {s.onDisk ? fmtBytes(s.sizeBytes) : 'file missing'}
                    </Badge>
                  </Group>
                ))}
              </Stack>
            )}
          </Card>
        </Grid.Col>
      </Grid>

      <Card withBorder padding={0}>
        <Tabs defaultValue="activity" onChange={(v) => { if (v === 'logs' && !logs) void loadLogs(); }}>
          <Tabs.List>
            <Tabs.Tab value="activity">Activity</Tabs.Tab>
            <Tabs.Tab value="logs">Container logs</Tabs.Tab>
          </Tabs.List>

          <Tabs.Panel value="activity" p="sm">
            {jobs.length === 0 ? <Text c="dimmed" size="sm">Nothing has run against this tenant yet.</Text> : (
              <Table fz="sm">
                <Table.Thead><Table.Tr>
                  <Table.Th>What</Table.Th><Table.Th>State</Table.Th><Table.Th>Step</Table.Th>
                  <Table.Th>Started</Table.Th><Table.Th>By</Table.Th>
                </Table.Tr></Table.Thead>
                <Table.Tbody>
                  {jobs.map((j) => (
                    <Table.Tr key={j.id} style={{ cursor: 'pointer' }} onClick={() => setJobId(j.id)}>
                      <Table.Td>{j.kind}</Table.Td>
                      <Table.Td><Badge size="sm" variant="light" color={JOB_COLOR[j.state] ?? 'gray'}>{j.state}</Badge></Table.Td>
                      <Table.Td><Text size="xs" c={j.error ? 'red' : undefined} lineClamp={1}>{j.error ?? j.step}</Text></Table.Td>
                      <Table.Td><Text size="xs">{fmt(j.startedAt ?? j.createdAt)}</Text></Table.Td>
                      <Table.Td><Text size="xs" c="dimmed">{j.createdBy ?? '—'}</Text></Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Tabs.Panel>

          <Tabs.Panel value="logs" p="sm">
            <Group justify="flex-end" mb="xs">
              <Button size="xs" variant="default" onClick={() => void loadLogs()}>Reload</Button>
            </Group>
            <Code block style={{ fontSize: 11, whiteSpace: 'pre-wrap', maxHeight: '60vh', overflow: 'auto' }}>
              {logs || 'Open this tab to load the last 400 lines.'}
            </Code>
          </Tabs.Panel>
        </Tabs>
      </Card>

      <Modal opened={Boolean(revealed)} onClose={() => setRevealed(null)} title="One-time administrator password">
        <Stack gap="sm">
          <Text size="sm">
            Already erased from the registry — closing this dialog loses it.
          </Text>
          <Code block>{revealed && `https://${tenant.slug}.zulo.one\n${revealed.user} / ${revealed.password}`}</Code>
          <Group justify="flex-end"><Button onClick={() => setRevealed(null)}>I have copied it</Button></Group>
        </Stack>
      </Modal>

      <Modal opened={destroy !== null} onClose={() => setDestroy(null)} title={destroy === 'release' ? 'Stop managing' : 'Delete tenant'}>
        <Stack gap="sm">
          {destroy === 'delete' ? (
            <Alert color="red" icon={<IconAlertTriangle size={16} />}>
              This destroys the container, the database, the role and the key ring. Snapshots survive, and are the
              only thing that would.
            </Alert>
          ) : (
            <Alert color="blue">
              The registry row goes. The container keeps running and the data stays exactly where it is — the panel
              simply stops managing this tenant.
            </Alert>
          )}
          {/* Both are offered here, because the difference only matters at this
              moment and getting it wrong destroys data. */}
          <Button variant="subtle" size="xs" onClick={() => setDestroy(destroy === 'delete' ? 'release' : 'delete')}>
            {destroy === 'delete' ? 'I only want to stop managing it →' : '← I really want to destroy it'}
          </Button>
          <TextInput label={`Type "${tenant.slug}" to confirm`} value={confirm} onChange={(e) => setConfirm(e.currentTarget.value)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setDestroy(null)}>Cancel</Button>
            <Button
              color={destroy === 'delete' ? 'red' : 'blue'} disabled={confirm !== tenant.slug}
              onClick={async () => {
                const mode = destroy; setDestroy(null);
                try {
                  if (mode === 'delete') await api.remove(id, tenant.slug);
                  else await api.release(id, tenant.slug);
                  location.href = '/tenants';
                } catch (e) { setError((e as Error).message); }
              }}
            >
              {destroy === 'delete' ? 'Destroy it' : 'Release it'}
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
