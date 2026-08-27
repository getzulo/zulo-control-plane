import { useCallback, useEffect, useState } from 'react';
import {
  ActionIcon, Alert, Badge, Button, Card, Code, Drawer, Group, Loader, Modal,
  Stack, Table, Text, TextInput, Title, Tooltip,
} from '@mantine/core';
import {
  IconAlertTriangle, IconFileText, IconPlayerPlay, IconPlayerStop,
  IconPlus, IconRefresh, IconRotate, IconTrash,
} from '@tabler/icons-react';
import { api, type FleetHealth, type Tenant } from './api';

const STATUS_COLOR: Record<string, string> = {
  Active: 'green', Provisioning: 'blue', Suspended: 'gray', Failed: 'red', Deleting: 'orange',
};

const fmt = (value?: string | null) => (value ? new Date(value).toLocaleString() : '—');

/** New-tenant form. Provisioning runs inline and takes minutes, so the dialog
 *  stays open and busy until the backend answers — closing early would hide the
 *  outcome of the thing the operator just started. */
function NewTenant({ opened, onClose, onCreated }: { opened: boolean; onClose: () => void; onCreated: () => void }) {
  const [slug, setSlug] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [adminEmail, setAdminEmail] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const slugOk = /^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/.test(slug);

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      await api.createTenant({ slug, displayName: displayName || undefined, adminEmail });
      onCreated();
      onClose();
      setSlug(''); setDisplayName(''); setAdminEmail('');
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={busy ? () => {} : onClose} title="New tenant" closeOnClickOutside={!busy}>
      <Stack gap="sm">
        <TextInput
          label="Slug" description="Becomes the subdomain: <slug>.zulo.one"
          placeholder="acme" value={slug} onChange={(e) => setSlug(e.currentTarget.value.toLowerCase())}
          error={slug && !slugOk ? 'Lowercase letters, digits and dashes only' : null}
          disabled={busy}
        />
        <TextInput label="Display name" placeholder="ACME Ltd" value={displayName}
          onChange={(e) => setDisplayName(e.currentTarget.value)} disabled={busy} />
        <TextInput
          label="Administrator e-mail" description="Gets the sign-in invitation"
          placeholder="ops@acme.com" value={adminEmail}
          onChange={(e) => setAdminEmail(e.currentTarget.value)} disabled={busy}
        />
        {busy && (
          <Alert color="blue" icon={<Loader size={16} />}>
            Creating the database, starting the container and waiting for it to boot.
            First boot runs migrations and a metadata compile — this takes minutes.
          </Alert>
        )}
        {error && <Alert color="red" icon={<IconAlertTriangle size={16} />}>{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button onClick={() => void submit()} loading={busy} disabled={!slugOk || !adminEmail}>Create</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

export default function App() {
  const [fleet, setFleet] = useState<FleetHealth | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);
  const [logsFor, setLogsFor] = useState<Tenant | null>(null);
  const [logs, setLogs] = useState('');
  const [confirmDelete, setConfirmDelete] = useState<Tenant | null>(null);
  const [confirmText, setConfirmText] = useState('');

  const refresh = useCallback(async () => {
    try {
      // The roll-up re-probes every active tenant, so this doubles as a health refresh.
      setFleet(await api.fleetHealth());
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void refresh();
    const timer = setInterval(() => void refresh(), 15_000);
    return () => clearInterval(timer);
  }, [refresh]);

  const act = async (action: () => Promise<unknown>) => {
    try { await action(); } catch (e) { setError((e as Error).message); }
    void refresh();
  };

  const openLogs = async (tenant: Tenant) => {
    setLogsFor(tenant);
    setLogs('Loading…');
    try { setLogs((await api.logs(tenant.id)).logs || '(empty)'); }
    catch (e) { setLogs(`Could not read logs: ${(e as Error).message}`); }
  };

  return (
    <Stack p="lg" gap="md">
      <Group justify="space-between">
        <div>
          <Title order={3}>ZuloOne fleet</Title>
          <Text size="sm" c="dimmed">Tenants, their state and what went wrong</Text>
        </div>
        <Group gap="xs">
          <Button variant="default" leftSection={<IconRefresh size={14} />} onClick={() => void refresh()}>Refresh</Button>
          <Button leftSection={<IconPlus size={14} />} onClick={() => setCreating(true)}>New tenant</Button>
        </Group>
      </Group>

      {error && (
        <Alert color="red" icon={<IconAlertTriangle size={16} />} withCloseButton onClose={() => setError(null)}>
          {error}
        </Alert>
      )}

      {fleet && (
        <Group gap="xs">
          <Badge size="lg" variant="light">Total: {fleet.total}</Badge>
          <Badge size="lg" color="green" variant="light">Active: {fleet.active}</Badge>
          <Badge size="lg" color="gray" variant="light">Suspended: {fleet.suspended}</Badge>
          <Badge size="lg" color="red" variant="light">Failed: {fleet.failed}</Badge>
          {/* Active but not answering — the one number that means someone is down right now. */}
          {fleet.down > 0 && <Badge size="lg" color="red">Down: {fleet.down}</Badge>}
        </Group>
      )}

      <Card withBorder padding={0}>
        {loading ? (
          <Group p="xl" justify="center"><Loader /></Group>
        ) : (
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Tenant</Table.Th>
                <Table.Th>Status</Table.Th>
                <Table.Th>Health</Table.Th>
                <Table.Th>Image</Table.Th>
                <Table.Th>Administrator</Table.Th>
                <Table.Th>Created</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {(fleet?.tenants ?? []).map((t) => (
                <Table.Tr key={t.id}>
                  <Table.Td>
                    <Text fw={600}>{t.slug}</Text>
                    {t.displayName && <Text size="xs" c="dimmed">{t.displayName}</Text>}
                    {/* A failed tenant is useless without its reason, so it rides in the row. */}
                    {t.lastError && <Text size="xs" c="red" lineClamp={2} title={t.lastError}>{t.lastError}</Text>}
                  </Table.Td>
                  <Table.Td><Badge color={STATUS_COLOR[t.status] ?? 'gray'} variant="light">{t.status}</Badge></Table.Td>
                  <Table.Td>
                    <Badge color={t.health === 'Ok' ? 'green' : t.health === 'Down' ? 'red' : 'gray'} variant="dot">
                      {t.health}
                    </Badge>
                  </Table.Td>
                  <Table.Td><Code>{t.imageTag}</Code></Table.Td>
                  <Table.Td><Text size="sm">{t.adminEmail ?? '—'}</Text></Table.Td>
                  <Table.Td><Text size="sm">{fmt(t.createdAt)}</Text></Table.Td>
                  <Table.Td>
                    <Group gap={4} wrap="nowrap" justify="flex-end">
                      {t.status === 'Suspended' ? (
                        <Tooltip label="Start">
                          <ActionIcon variant="subtle" onClick={() => void act(() => api.start(t.id))}><IconPlayerPlay size={16} /></ActionIcon>
                        </Tooltip>
                      ) : (
                        <Tooltip label="Stop">
                          <ActionIcon variant="subtle" onClick={() => void act(() => api.stop(t.id))}><IconPlayerStop size={16} /></ActionIcon>
                        </Tooltip>
                      )}
                      <Tooltip label="Restart">
                        <ActionIcon variant="subtle" onClick={() => void act(() => api.restart(t.id))}><IconRotate size={16} /></ActionIcon>
                      </Tooltip>
                      <Tooltip label="Logs">
                        <ActionIcon variant="subtle" onClick={() => void openLogs(t)}><IconFileText size={16} /></ActionIcon>
                      </Tooltip>
                      <Tooltip label="Delete">
                        <ActionIcon variant="subtle" color="red" onClick={() => { setConfirmDelete(t); setConfirmText(''); }}>
                          <IconTrash size={16} />
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
              {(fleet?.tenants ?? []).length === 0 && (
                <Table.Tr><Table.Td colSpan={7}><Text ta="center" c="dimmed" py="lg">No tenants yet</Text></Table.Td></Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        )}
      </Card>

      <NewTenant opened={creating} onClose={() => setCreating(false)} onCreated={() => void refresh()} />

      <Drawer opened={Boolean(logsFor)} onClose={() => setLogsFor(null)} position="right" size="xl"
        title={`Logs — ${logsFor?.slug ?? ''}`}>
        <Code block style={{ fontSize: 11, whiteSpace: 'pre-wrap', maxHeight: '80vh', overflow: 'auto' }}>{logs}</Code>
      </Drawer>

      {/* Deleting drops the tenant's database and there is no backup step until
          Phase 2, so the slug is retyped rather than merely confirmed. */}
      <Modal opened={Boolean(confirmDelete)} onClose={() => setConfirmDelete(null)} title="Delete tenant">
        <Stack gap="sm">
          <Alert color="red" icon={<IconAlertTriangle size={16} />}>
            This destroys the container, the database and all of its data. There is no backup yet.
          </Alert>
          <TextInput label={`Type "${confirmDelete?.slug}" to confirm`} value={confirmText}
            onChange={(e) => setConfirmText(e.currentTarget.value)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setConfirmDelete(null)}>Cancel</Button>
            <Button color="red" disabled={confirmText !== confirmDelete?.slug}
              onClick={() => { const t = confirmDelete!; setConfirmDelete(null); void act(() => api.remove(t.id, t.slug)); }}>
              Delete
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
