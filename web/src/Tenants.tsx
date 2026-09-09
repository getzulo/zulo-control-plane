import { useCallback, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import {
  Alert, Badge, Button, Card, Code, Group, Loader, Modal, Stack, Table, Text, TextInput, Title,
} from '@mantine/core';
import { IconAlertTriangle, IconLink, IconPlus } from '@tabler/icons-react';
import { api, type FleetHealth } from './api';
import { JobProgress, STATUS_COLOR, fmt, useJob, usePoll } from './shared';

export function TenantsPage() {
  const navigate = useNavigate();
  const [fleet, setFleet] = useState<FleetHealth | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [creating, setCreating] = useState(false);
  const [adopting, setAdopting] = useState(false);
  const [jobId, setJobId] = useState<string | null>(null);
  const job = useJob(jobId);

  const refresh = useCallback(async () => {
    try { setFleet(await api.fleetHealth()); setError(null); }
    catch (e) { setError((e as Error).message); }
    finally { setLoading(false); }
  }, []);

  usePoll(refresh, 12_000);

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <div>
          <Title order={3}>Tenants</Title>
          <Text size="sm" c="dimmed">The fleet, its state, and what went wrong</Text>
        </div>
        <Group gap="xs">
          {/* Adoption is not a variant of creation: it takes over something that
              already exists, and mixing the two invites doing one meaning the other. */}
          <Button variant="default" leftSection={<IconLink size={14} />} onClick={() => setAdopting(true)}>
            Adopt existing
          </Button>
          <Button leftSection={<IconPlus size={14} />} onClick={() => setCreating(true)}>New tenant</Button>
        </Group>
      </Group>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />} withCloseButton onClose={() => setError(null)}>{error}</Alert>}

      {fleet && (
        <Group gap="xs">
          <Badge size="lg" variant="light">Total: {fleet.total}</Badge>
          <Badge size="lg" color="green" variant="light">Active: {fleet.active}</Badge>
          <Badge size="lg" color="gray" variant="light">Suspended: {fleet.suspended}</Badge>
          <Badge size="lg" color="red" variant="light">Failed: {fleet.failed}</Badge>
          {fleet.down > 0 && <Badge size="lg" color="red">Down: {fleet.down}</Badge>}
        </Group>
      )}

      {job && (
        <Card withBorder padding="sm">
          <JobProgress job={job} />
          {(job.state === 'Succeeded' || job.state === 'Failed') && (
            <Button size="xs" variant="subtle" mt={6} onClick={() => { setJobId(null); void refresh(); }}>Dismiss</Button>
          )}
        </Card>
      )}

      <Card withBorder padding={0}>
        {loading ? <Group p="xl" justify="center"><Loader /></Group> : (
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Tenant</Table.Th><Table.Th>Status</Table.Th><Table.Th>Health</Table.Th>
                <Table.Th>Image</Table.Th><Table.Th>Administrator</Table.Th><Table.Th>Created</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {(fleet?.tenants ?? []).map((t) => (
                <Table.Tr key={t.id} style={{ cursor: 'pointer' }} onClick={() => navigate(`/tenants/${t.id}`)}>
                  <Table.Td>
                    <Group gap={6}>
                      <Text fw={600}>{t.slug}</Text>
                      {t.restoredFromSlug && <Badge size="xs" color="grape" variant="light">copy</Badge>}
                    </Group>
                    {t.displayName && <Text size="xs" c="dimmed">{t.displayName}</Text>}
                    {t.lastError && <Text size="xs" c="red" lineClamp={2}>{t.lastError}</Text>}
                  </Table.Td>
                  <Table.Td><Badge color={STATUS_COLOR[t.status] ?? 'gray'} variant="light">{t.status}</Badge></Table.Td>
                  <Table.Td>
                    <Badge color={t.health === 'Ok' ? 'green' : t.health === 'Down' ? 'red' : 'gray'} variant="dot">
                      {t.health}
                    </Badge>
                  </Table.Td>
                  <Table.Td><Code style={{ fontSize: 11 }}>{t.imageTag.split(':').pop()}</Code></Table.Td>
                  <Table.Td>
                    <Text size="sm">{t.adminEmail ?? '—'}</Text>
                    {t.hasUnreadAdminPassword && <Text size="xs" c="blue">one-time password waiting</Text>}
                  </Table.Td>
                  <Table.Td><Text size="sm">{fmt(t.createdAt)}</Text></Table.Td>
                </Table.Tr>
              ))}
              {(fleet?.tenants ?? []).length === 0 && (
                <Table.Tr><Table.Td colSpan={6}><Text ta="center" c="dimmed" py="lg">No tenants yet</Text></Table.Td></Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        )}
      </Card>

      <NewTenant opened={creating} onClose={() => setCreating(false)} onJob={(id) => { setJobId(id); void refresh(); }} />
      <AdoptTenant opened={adopting} onClose={() => setAdopting(false)} onJob={(id) => { setJobId(id); void refresh(); }} />
    </Stack>
  );
}

function NewTenant({ opened, onClose, onJob }: { opened: boolean; onClose: () => void; onJob: (id: string) => void }) {
  const [slug, setSlug] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [adminEmail, setAdminEmail] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const slugOk = /^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/.test(slug);

  const submit = async () => {
    setBusy(true); setError(null);
    try {
      const r = await api.createTenant({ slug, displayName: displayName || undefined, adminEmail });
      onJob(r.jobId); onClose();
      setSlug(''); setDisplayName(''); setAdminEmail('');
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  };

  return (
    <Modal opened={opened} onClose={onClose} title="New tenant">
      <Stack gap="sm">
        <TextInput
          label="Slug" description="Becomes the subdomain: <slug>.zulo.one" placeholder="acme"
          value={slug} onChange={(e) => setSlug(e.currentTarget.value.toLowerCase())}
          error={slug && !slugOk ? 'Lowercase letters, digits and dashes only' : null} disabled={busy}
        />
        <TextInput label="Display name" placeholder="ACME Ltd" value={displayName} onChange={(e) => setDisplayName(e.currentTarget.value)} disabled={busy} />
        <TextInput label="Administrator e-mail" description="Gets the sign-in invitation" placeholder="ops@acme.com"
          value={adminEmail} onChange={(e) => setAdminEmail(e.currentTarget.value)} disabled={busy} />
        <Text size="xs" c="dimmed">
          The slug is claimed immediately; the database, container and first boot happen on a worker and take a few
          minutes. Watch it on the progress bar.
        </Text>
        {error && <Alert color="red" icon={<IconAlertTriangle size={16} />}>{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button onClick={() => void submit()} loading={busy} disabled={!slugOk || !adminEmail}>Create</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Takes over a tenant deployed before the panel existed. */
function AdoptTenant({ opened, onClose, onJob }: { opened: boolean; onClose: () => void; onJob: (id: string) => void }) {
  const [slug, setSlug] = useState('');
  const [containerName, setContainerName] = useState('');
  const [adminEmail, setAdminEmail] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    setBusy(true); setError(null);
    try {
      const r = await api.adoptTenant({ slug, adminEmail: adminEmail || undefined, containerName: containerName || undefined });
      onJob(r.jobId); onClose(); setSlug(''); setContainerName('');
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  };

  return (
    <Modal opened={opened} onClose={onClose} title="Adopt an existing tenant">
      <Stack gap="sm">
        <Alert color="blue">
          For a tenant that already has a database and a running container but no registry row. The panel takes over
          its database password and recreates the container from the row, so <b>its signed-in users are logged
          out</b>. Its data is not touched.
        </Alert>
        <TextInput label="Slug" placeholder="t1" value={slug} onChange={(e) => setSlug(e.currentTarget.value.toLowerCase())} disabled={busy} />
        <TextInput
          label="Container name" description="Only if it is not zuloone-tenant-<slug>"
          placeholder="zuloone-prod-tenant-t1-1" value={containerName}
          onChange={(e) => setContainerName(e.currentTarget.value)} disabled={busy}
        />
        <TextInput label="Administrator e-mail" placeholder="ops@example.com" value={adminEmail} onChange={(e) => setAdminEmail(e.currentTarget.value)} disabled={busy} />
        <Text size="xs" c="dimmed">
          Changing a role's password needs ADMIN OPTION on it, which the panel cannot grant itself. If adoption fails
          with that, the error names the exact GRANT to run once as a superuser.
        </Text>
        {error && <Alert color="red" icon={<IconAlertTriangle size={16} />}>{error}</Alert>}
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} disabled={busy}>Cancel</Button>
          <Button onClick={() => void submit()} loading={busy} disabled={!slug}>Adopt</Button>
        </Group>
      </Stack>
    </Modal>
  );
}
