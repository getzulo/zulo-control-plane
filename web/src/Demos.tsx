import { useCallback, useState } from 'react';
import {
  Alert, Badge, Button, Card, Code, Group, Loader, Modal, Stack, Table, Text, TextInput, Title,
} from '@mantine/core';
import { IconAlertTriangle, IconCopy, IconPlayerPlay, IconRefresh, IconTrash } from '@tabler/icons-react';
import { api, type DemoClaim, type DemoOverview } from './api';
import { fmt, usePoll } from './shared';

const DEMO_COLOR: Record<string, string> = { Golden: 'yellow', Pooled: 'cyan', Claimed: 'green' };

export function DemosPage() {
  const [data, setData] = useState<DemoOverview | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [email, setEmail] = useState('');
  const [company, setCompany] = useState('');
  const [claim, setClaim] = useState<DemoClaim | null>(null);

  const refresh = useCallback(async () => {
    try { setData(await api.demoOverview()); setError(null); }
    catch (e) { setError((e as Error).message); }
  }, []);
  usePoll(refresh, 8_000);

  const action = async (name: string, fn: () => Promise<unknown>) => {
    setBusy(name); setError(null);
    try { await fn(); await refresh(); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(null); }
  };

  const issue = async () => {
    setBusy('claim'); setError(null);
    try {
      setClaim(await api.demoClaim(email, company));
      setEmail(''); setCompany('');
      await refresh();
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(null); }
  };

  if (!data && !error) return <Group justify="center" p="xl"><Loader /></Group>;

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <div>
          <Title order={3}>Demos</Title>
          <Text size="sm" c="dimmed">Golden template, warm pool and disposable workspaces</Text>
        </div>
        <Group gap="xs">
          <Button variant="default" leftSection={<IconRefresh size={14} />} loading={busy === 'template'}
            onClick={() => void action('template', api.demoTemplate)}>Rebuild template</Button>
          <Button leftSection={<IconPlayerPlay size={14} />} loading={busy === 'provision'}
            onClick={() => void action('provision', api.demoProvision)}>Build one</Button>
          <Button color="orange" variant="light" loading={busy === 'drain'}
            onClick={() => void action('drain', api.demoDrain)}>Drain pool</Button>
        </Group>
      </Group>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />}>{error}</Alert>}
      {!data?.enabled && <Alert color="yellow">Demo workspaces are disabled in Settings.</Alert>}

      {data && (
        <Group gap="xs">
          <Badge size="lg" variant="light">Ready: {data.tenants.filter((t) => t.demo === 'Pooled').length}</Badge>
          <Badge size="lg" color="green" variant="light">Claimed: {data.tenants.filter((t) => t.demo === 'Claimed').length}</Badge>
          <Badge size="lg" color="yellow" variant="light">Golden: {data.tenants.filter((t) => t.demo === 'Golden').length}</Badge>
          <Badge size="lg" color={data.queued ? 'orange' : 'gray'} variant="light">Queued: {data.queued}</Badge>
          <Text size="xs" c="dimmed">target {data.poolTarget}, ceiling {data.maxConcurrent}</Text>
        </Group>
      )}

      <Card withBorder>
        <Text fw={600} mb="xs">Issue a demo manually</Text>
        <Group align="end">
          <TextInput label="E-mail" placeholder="person@example.com" value={email}
            onChange={(e) => setEmail(e.currentTarget.value)} style={{ flex: 1 }} />
          <TextInput label="Company" value={company}
            onChange={(e) => setCompany(e.currentTarget.value)} style={{ flex: 1 }} />
          <Button loading={busy === 'claim'} disabled={!email.includes('@')} onClick={() => void issue()}>Claim ready demo</Button>
        </Group>
      </Card>

      <Card withBorder padding={0}>
        <Table striped highlightOnHover>
          <Table.Thead><Table.Tr>
            <Table.Th>Workspace</Table.Th><Table.Th>Kind</Table.Th><Table.Th>Status</Table.Th>
            <Table.Th>Expires</Table.Th><Table.Th>Request</Table.Th><Table.Th />
          </Table.Tr></Table.Thead>
          <Table.Tbody>
            {(data?.tenants ?? []).map((t) => (
              <Table.Tr key={t.id}>
                <Table.Td><Text fw={600}>{t.slug}</Text><Text size="xs" c="dimmed">{fmt(t.createdAt)}</Text></Table.Td>
                <Table.Td><Badge color={DEMO_COLOR[t.demo] ?? 'gray'} variant="light">{t.demo}</Badge></Table.Td>
                <Table.Td>{t.status} · {t.health}</Table.Td>
                <Table.Td>{fmt(t.expiresAt)}</Table.Td>
                <Table.Td><Code fz={10}>{t.demoRequestId?.slice(0, 8) ?? '—'}</Code></Table.Td>
                <Table.Td>
                  {t.demo === 'Claimed' && <Button size="xs" variant="subtle"
                    onClick={() => void action(`extend:${t.id}`, () => api.demoExtend(t.id, 24))}>+24h</Button>}
                  {t.demo !== 'Golden' && <Button size="xs" color="red" variant="subtle" leftSection={<IconTrash size={13} />}
                    onClick={() => {
                      const confirmSlug = window.prompt(`Retype ${t.slug} to destroy this demo:`);
                      if (confirmSlug) void action(`kill:${t.id}`, () => api.demoKill(t.id, confirmSlug));
                    }}>Kill</Button>}
                </Table.Td>
              </Table.Tr>
            ))}
            {(data?.tenants ?? []).length === 0 && <Table.Tr><Table.Td colSpan={6}><Text ta="center" c="dimmed" py="lg">No demo workspaces yet</Text></Table.Td></Table.Tr>}
          </Table.Tbody>
        </Table>
      </Card>

      <Modal opened={claim !== null} onClose={() => setClaim(null)} title="Demo issued" centered>
        {claim && <Stack gap="xs">
          <Text>The password is shown here; keep it out of tickets and logs.</Text>
          <Text size="sm">URL</Text><Code>{claim.url}</Code>
          <Text size="sm">User</Text><Code>{claim.user}</Code>
          <Text size="sm">Password</Text>
          <Group gap="xs"><Code style={{ flex: 1 }}>{claim.password}</Code><Button size="xs" variant="light"
            leftSection={<IconCopy size={13} />} onClick={() => void navigator.clipboard.writeText(claim.password)}>Copy</Button></Group>
          <Text size="xs" c="dimmed">Expires {fmt(claim.expiresAt)}</Text>
        </Stack>}
      </Modal>
    </Stack>
  );
}
