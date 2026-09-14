import { useCallback, useMemo, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import {
  Alert, Badge, Button, Card, Code, Group, Modal, NumberInput, Select, SimpleGrid, Stack, Switch,
  Table, Text, TextInput, Title,
} from '@mantine/core';
import { IconAlertTriangle, IconNotes, IconTrash } from '@tabler/icons-react';
import { api, type LogEventRow, type LogStoreStatus, type LogTenantRow } from './api';
import { afterInputEvent, fmt, fmtBytes, inputChecked, usePoll } from './shared';

const LEVELS = ['Verbose', 'Debug', 'Information', 'Warning', 'Error', 'Fatal'];
const CHANNELS = ['', 'Http', 'Script', 'Job', 'Agent', 'System'];
const LEVEL_COLOR: Record<string, string> = {
  Verbose: 'gray', Debug: 'gray', Information: 'blue', Warning: 'yellow', Error: 'red', Fatal: 'red',
};

function hoursAgoIso(h: number) {
  return new Date(Date.now() - h * 3600_000).toISOString();
}

function startOfTodayIso() {
  const d = new Date();
  d.setHours(0, 0, 0, 0);
  return d.toISOString();
}

export function LogsPage() {
  const [params, setParams] = useSearchParams();
  const slug = params.get('slug') ?? '';

  const [status, setStatus] = useState<LogStoreStatus | null>(null);
  const [rows, setRows] = useState<LogTenantRow[]>([]);
  const [events, setEvents] = useState<LogEventRow[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [selected, setSelected] = useState<LogEventRow | null>(null);

  const [preset, setPreset] = useState('24h');
  const [minLevel, setMinLevel] = useState<string | null>('Information');
  const [channel, setChannel] = useState<string | null>('');
  const [source, setSource] = useState('');
  const [text, setText] = useState('');
  const [userName, setUserName] = useState('');
  const [requestId, setRequestId] = useState('');
  const [jobId, setJobId] = useState(params.get('jobId') ?? '');
  const [agentId, setAgentId] = useState('');
  const [tail, setTail] = useState(false);

  const [purge, setPurge] = useState<{ slug: string; all: boolean } | null>(null);
  const [confirm, setConfirm] = useState('');
  const [ttlSlug, setTtlSlug] = useState<string | null>(null);
  const [ttlDays, setTtlDays] = useState(14);

  const windowUtc = useMemo(() => {
    const to = new Date().toISOString();
    if (preset === '1h') return { fromUtc: hoursAgoIso(1), toUtc: to };
    if (preset === 'today') return { fromUtc: startOfTodayIso(), toUtc: to };
    if (preset === '7d') return { fromUtc: hoursAgoIso(24 * 7), toUtc: to };
    return { fromUtc: hoursAgoIso(24), toUtc: to };
  }, [preset]);

  const refreshFleet = useCallback(async () => {
    try {
      const [s, t] = await Promise.all([api.logStatus(), api.logTenants()]);
      setStatus(s); setRows(t); setError(null);
    } catch (e) { setError((e as Error).message); }
  }, []);

  const refreshEvents = useCallback(async () => {
    try {
      const res = await api.logEvents({
        slug: slug || undefined,
        ...windowUtc,
        minLevel: minLevel || undefined,
        channel: channel || undefined,
        sourceContains: source || undefined,
        text: text || undefined,
        userName: userName || undefined,
        requestId: requestId || undefined,
        jobId: jobId || undefined,
        agentId: agentId || undefined,
        take: 100,
      });
      setEvents(res.items);
      setSelected((cur) => (cur && res.items.some((x) => x.id === cur.id) ? cur : null));
      setError(null);
    } catch (e) { setError((e as Error).message); }
  }, [slug, windowUtc, minLevel, channel, source, text, userName, requestId, jobId, agentId]);

  usePoll(refreshFleet, 45_000);
  usePoll(refreshEvents, tail ? 3_000 : 20_000);

  const setSlug = (next: string) => {
    const p = new URLSearchParams(params);
    if (next) p.set('slug', next); else p.delete('slug');
    setParams(p, { replace: true });
  };

  const filterBy = (field: 'requestId' | 'jobId' | 'agentId', value: string) => {
    if (field === 'requestId') setRequestId(value);
    if (field === 'jobId') setJobId(value);
    if (field === 'agentId') setAgentId(value);
  };

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <div>
          <Title order={3}>Logs</Title>
          <Text size="sm" c="dimmed">
            Structured journal per tenant. Container stdout is still on the tenant card — this is
            the Mongo stream.
          </Text>
        </div>
      </Group>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />}>{error}</Alert>}

      {status?.state === 'disconnected' && (
        <Alert color="yellow" title="Journal is not connected">
          {status.error ?? 'TenantLogs:Url is empty or Mongo is unreachable. The page stays up; there is nothing to query until the farm Mongo is wired.'}
        </Alert>
      )}

      <SimpleGrid cols={{ base: 2, sm: 4 }}>
        <Meter label="Mongo" value={status?.state === 'connected' ? 'connected' : 'disconnected'} />
        <Meter label="Journal disk" value={fmtBytes(status?.clusterSizeBytes)} />
        <Meter label="Tenants" value={status ? String(status.tenantCount) : '—'} />
        <Meter
          label="Missing database"
          value={status ? String(status.missingDatabaseCount) : '—'}
          hint="provision never created logs_<slug>"
        />
      </SimpleGrid>

      <Card withBorder padding="md">
        <Text fw={600} mb="xs">Storage</Text>
        {rows.length === 0 ? <Text size="sm" c="dimmed">No tenants in the registry.</Text> : (
          <Table fz="sm" highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Tenant</Table.Th>
                <Table.Th>Size</Table.Th>
                <Table.Th>Docs</Table.Th>
                <Table.Th>Oldest</Table.Th>
                <Table.Th>Newest</Table.Th>
                <Table.Th>TTL</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {rows.map((r) => (
                <Table.Tr
                  key={r.slug}
                  style={{ cursor: 'pointer' }}
                  bg={slug === r.slug ? 'var(--mantine-color-blue-light)' : undefined}
                  onClick={() => setSlug(r.slug === slug ? '' : r.slug)}
                >
                  <Table.Td>
                    <Group gap={6}>
                      <Text size="sm" fw={500}>{r.slug}</Text>
                      <Badge size="xs" variant="light">{r.status}</Badge>
                      {!r.databaseExists && <Badge size="xs" color="orange">no database</Badge>}
                      {r.sinkSilent && <Badge size="xs" color="red">sink silent</Badge>}
                    </Group>
                  </Table.Td>
                  <Table.Td>{r.databaseExists ? fmtBytes(r.sizeBytes) : '—'}</Table.Td>
                  <Table.Td>{r.documents ?? '—'}</Table.Td>
                  <Table.Td><Text size="xs">{fmt(r.oldestUtc)}</Text></Table.Td>
                  <Table.Td><Text size="xs">{fmt(r.newestUtc)}</Text></Table.Td>
                  <Table.Td>{r.ttlDays != null ? `${r.ttlDays}d` : '—'}</Table.Td>
                  <Table.Td>
                    <Group gap={4} justify="flex-end" onClick={(e) => e.stopPropagation()}>
                      <Button size="compact-xs" variant="default" component={Link} to={`/tenants/${r.tenantId}`}>
                        tenant
                      </Button>
                      <Button size="compact-xs" variant="light" disabled={!r.databaseExists}
                        onClick={() => { setTtlSlug(r.slug); setTtlDays(r.ttlDays ?? 14); }}>
                        TTL
                      </Button>
                      <Button size="compact-xs" variant="light" color="orange" disabled={!r.databaseExists}
                        onClick={() => { setPurge({ slug: r.slug, all: false }); setConfirm(''); }}>
                        older
                      </Button>
                      <Button size="compact-xs" variant="light" color="red" disabled={!r.databaseExists}
                        leftSection={<IconTrash size={12} />}
                        onClick={() => { setPurge({ slug: r.slug, all: true }); setConfirm(''); }}>
                        clear
                      </Button>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
            </Table.Tbody>
          </Table>
        )}
        <Text size="xs" c="dimmed" mt="xs">Click a row to filter events to that tenant. Click again for the whole farm.</Text>
      </Card>

      <Card withBorder padding="md">
        <Group justify="space-between" mb="sm">
          <Group gap="xs">
            <IconNotes size={16} />
            <Text fw={600}>Events {slug ? `· ${slug}` : '· all tenants'}</Text>
          </Group>
          <Group gap="xs">
            <Switch size="xs" label="Tail" checked={tail} onChange={(e) => {
              const on = inputChecked(e);
              afterInputEvent(() => setTail(on));
            }} />
            <Button size="xs" variant="default" onClick={() => void refreshEvents()}>Refresh</Button>
          </Group>
        </Group>

        <Group gap="xs" mb="sm" align="flex-end">
          <Select size="xs" w={110} label="Period" value={preset} onChange={(v) => setPreset(v ?? '24h')}
            data={[{ value: '1h', label: 'Hour' }, { value: 'today', label: 'Today' }, { value: '24h', label: '24 h' }, { value: '7d', label: '7 days' }]} />
          <Select size="xs" w={140} label="Min level" value={minLevel} onChange={setMinLevel} data={LEVELS} />
          <Select size="xs" w={120} label="Channel" value={channel} onChange={setChannel}
            data={CHANNELS.map((c) => ({ value: c, label: c || 'Any' }))} />
          <TextInput size="xs" w={160} label="Source" value={source} onChange={(e) => setSource(e.currentTarget.value)} />
          <TextInput size="xs" w={180} label="Text" value={text} onChange={(e) => setText(e.currentTarget.value)} />
          <TextInput size="xs" w={120} label="User" value={userName} onChange={(e) => setUserName(e.currentTarget.value)} />
          <TextInput size="xs" w={140} label="Request" value={requestId} onChange={(e) => setRequestId(e.currentTarget.value)} />
          <TextInput size="xs" w={160} label="Job" value={jobId} onChange={(e) => setJobId(e.currentTarget.value)} />
          <TextInput size="xs" w={160} label="Agent" value={agentId} onChange={(e) => setAgentId(e.currentTarget.value)} />
        </Group>

        <Text size="xs" c="dimmed" mb="xs">Showing {events.length}</Text>

        <Table fz="xs" highlightOnHover>
          <Table.Thead>
            <Table.Tr>
              <Table.Th>When</Table.Th>
              {!slug && <Table.Th>Tenant</Table.Th>}
              <Table.Th>Level</Table.Th>
              <Table.Th>Channel</Table.Th>
              <Table.Th>Source</Table.Th>
              <Table.Th>User</Table.Th>
              <Table.Th>Message</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {events.length === 0 ? (
              <Table.Tr>
                <Table.Td colSpan={slug ? 6 : 7}>
                  <Text c="dimmed">
                    {status?.state === 'disconnected'
                      ? 'Nothing to read until Mongo is connected.'
                      : 'No events in this window.'}
                  </Text>
                </Table.Td>
              </Table.Tr>
            ) : events.map((e) => (
              <Table.Tr
                key={`${e.slug}-${e.id}`}
                style={{ cursor: 'pointer' }}
                bg={selected?.id === e.id && selected.slug === e.slug ? 'var(--mantine-color-blue-light)' : undefined}
                onClick={() => setSelected(e)}
              >
                <Table.Td style={{ whiteSpace: 'nowrap' }}>{fmt(e.timestamp)}</Table.Td>
                {!slug && <Table.Td>{e.slug}</Table.Td>}
                <Table.Td><Badge size="xs" color={LEVEL_COLOR[e.level] ?? 'gray'}>{e.level}</Badge></Table.Td>
                <Table.Td>{e.channel}</Table.Td>
                <Table.Td><Text lineClamp={1}>{e.sourceContext ?? '—'}</Text></Table.Td>
                <Table.Td>{e.userName ?? '—'}</Table.Td>
                <Table.Td><Text lineClamp={1}>{e.message}</Text></Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>

        {selected && (
          <Card withBorder mt="sm" padding="sm">
            <Group justify="space-between" mb="xs">
              <Text fw={600} size="sm">{selected.level} · {selected.channel} · {selected.slug}</Text>
              <Button size="compact-xs" variant="default" onClick={() => {
                void navigator.clipboard.writeText(JSON.stringify(selected, null, 2));
              }}>Copy JSON</Button>
            </Group>
            <Text size="sm" mb="xs">{selected.message}</Text>
            {selected.exception && (
              <Code block style={{ fontSize: 11, maxHeight: 200, overflow: 'auto', marginBottom: 8 }}>
                {selected.exception}
              </Code>
            )}
            <Table fz="xs" withRowBorders={false}>
              <Table.Tbody>
                {([
                  ['RequestId', selected.requestId],
                  ['JobId', selected.jobId],
                  ['AgentId', selected.agentId],
                  ['User', selected.userName],
                  ['Source', selected.sourceContext],
                  ['Application', selected.application],
                ] as const).map(([k, v]) => v ? (
                  <Table.Tr key={k}>
                    <Table.Td c="dimmed" w={120}>{k}</Table.Td>
                    <Table.Td>
                      {(k === 'RequestId' || k === 'JobId' || k === 'AgentId') ? (
                        <Text size="xs" style={{ cursor: 'pointer' }} c="blue"
                          onClick={() => filterBy(k === 'RequestId' ? 'requestId' : k === 'JobId' ? 'jobId' : 'agentId', v)}>
                          {v}
                        </Text>
                      ) : v}
                    </Table.Td>
                  </Table.Tr>
                ) : null)}
                {Object.entries(selected.properties).map(([k, v]) => (
                  <Table.Tr key={k}>
                    <Table.Td c="dimmed">{k}</Table.Td>
                    <Table.Td><Text size="xs">{v ?? '—'}</Text></Table.Td>
                  </Table.Tr>
                ))}
              </Table.Tbody>
            </Table>
          </Card>
        )}
      </Card>

      <Modal opened={Boolean(purge)} onClose={() => setPurge(null)}
        title={purge?.all ? `Clear all logs · ${purge.slug}` : `Delete older than 14 days · ${purge?.slug}`}>
        <Stack>
          <Text size="sm">
            {purge?.all
              ? 'Drops the events collection and recreates indexes. The database and user stay — Core can keep writing.'
              : 'Deletes events older than 14 days. Does not drop the database.'}
          </Text>
          <TextInput label={`Type "${purge?.slug}" to confirm`} value={confirm} onChange={(e) => setConfirm(e.currentTarget.value)} />
          <Button color="red" disabled={confirm !== purge?.slug} onClick={async () => {
            if (!purge) return;
            try {
              await api.purgeLogs(purge.slug, {
                all: purge.all,
                olderThanDays: purge.all ? undefined : 14,
                confirmSlug: confirm,
              });
              setPurge(null);
              void refreshFleet();
              void refreshEvents();
            } catch (e) { setError((e as Error).message); }
          }}>
            {purge?.all ? 'Clear everything' : 'Delete older events'}
          </Button>
        </Stack>
      </Modal>

      <Modal opened={Boolean(ttlSlug)} onClose={() => setTtlSlug(null)} title={`TTL · ${ttlSlug}`}>
        <Stack>
          <NumberInput
            label="Days"
            value={ttlDays}
            min={1}
            max={3650}
            step={1}
            allowDecimal={false}
            onChange={(n) => setTtlDays(typeof n === 'number' ? n : Number(n) || 1)}
          />
          <Button onClick={async () => {
            if (!ttlSlug) return;
            try {
              await api.setLogTtl(ttlSlug, ttlDays);
              setTtlSlug(null);
              void refreshFleet();
            } catch (e) { setError((e as Error).message); }
          }}>Save TTL</Button>
        </Stack>
      </Modal>
    </Stack>
  );
}

function Meter({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <Card withBorder padding="sm">
      <Text size="xs" c="dimmed">{label}</Text>
      <Text fw={600}>{value}</Text>
      {hint && <Text size="xs" c="dimmed">{hint}</Text>}
    </Card>
  );
}
