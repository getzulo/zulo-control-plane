import { useCallback, useState } from 'react';
import {
  ActionIcon, Alert, Anchor, Badge, Button, Card, Code, Group, Loader, Modal, Select,
  Stack, Table, Text, TextInput, Title, Tooltip,
} from '@mantine/core';
import { IconAlertTriangle, IconArrowBackUp, IconRestore, IconTrash } from '@tabler/icons-react';
import { api, type Snapshot, type Tenant } from './api';
import { JobProgress, fmt, fmtBytes, useJob, usePoll } from './shared';

const KIND_COLOR: Record<string, string> = { Manual: 'blue', PreUpgrade: 'orange', PreSwap: 'grape' };

export function SnapshotsPage() {
  const [snapshots, setSnapshots] = useState<Snapshot[]>([]);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const [takeFor, setTakeFor] = useState<string | null>(null);
  const [note, setNote] = useState('');
  const [jobId, setJobId] = useState<string | null>(null);
  const [confirmDelete, setConfirmDelete] = useState<Snapshot | null>(null);

  const job = useJob(jobId);

  const refresh = useCallback(async () => {
    try {
      const [s, t] = await Promise.all([api.snapshots(), api.listTenants()]);
      setSnapshots(s); setTenants(t); setError(null);
    } catch (e) { setError((e as Error).message); }
    finally { setLoading(false); }
  }, []);

  usePoll(refresh, 15_000);

  // A restored copy is a tenant in every mechanical sense, so the list has to say
  // which rows are copies awaiting a decision rather than customers.
  const copies = tenants.filter((t) => t.restoredFromSlug);
  const real = tenants.filter((t) => !t.restoredFromSlug);
  const withPrevious = real.filter((t) => t.previousDatabaseName);

  const take = async () => {
    if (!takeFor) return;
    try {
      const r = await api.takeSnapshot(takeFor, note || undefined);
      setJobId(r.jobId); setTakeFor(null); setNote('');
    } catch (e) { setError((e as Error).message); }
  };

  const restore = async (s: Snapshot) => {
    try { setJobId((await api.restoreSnapshot(s.id)).jobId); }
    catch (e) { setError((e as Error).message); }
  };

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <div>
          <Title order={3}>Backups</Title>
          <Text size="sm" c="dimmed">Dumps, and the three steps that put one back</Text>
        </div>
        <Select
          placeholder="Snapshot a tenant…" w={240} searchable clearable
          data={real.filter((t) => t.databaseName).map((t) => ({ value: t.id, label: t.slug }))}
          value={takeFor} onChange={setTakeFor}
        />
      </Group>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />} withCloseButton onClose={() => setError(null)}>{error}</Alert>}

      {job && (
        <Card withBorder padding="sm">
          <JobProgress job={job} />
          {job.state === 'Succeeded' && (
            <Anchor size="sm" mt={6} onClick={() => { setJobId(null); void refresh(); }}>Dismiss</Anchor>
          )}
        </Card>
      )}

      {/* The middle step made visible. A restored copy exists only to be looked at,
          and it costs a container and a database until somebody decides. */}
      {copies.length > 0 && (
        <Card withBorder padding="md" style={{ borderColor: 'var(--mantine-color-grape-6)' }}>
          <Text fw={600} mb="xs">Restored copies awaiting a decision</Text>
          <Text size="sm" c="dimmed" mb="sm">
            Each is a full tenant on its own hostname. Sign in, confirm the data is what you expected, then either
            swap it in — which replaces the live tenant's data and keeps the old database as the undo — or discard it.
          </Text>
          <Stack gap="xs">
            {copies.map((c) => (
              <RestoredCopy key={c.id} copy={c} tenants={real} onDone={() => void refresh()} onJob={setJobId} onError={setError} />
            ))}
          </Stack>
        </Card>
      )}

      {withPrevious.length > 0 && (
        <Card withBorder padding="md">
          <Text fw={600} mb="xs">Databases set aside by a swap</Text>
          <Stack gap="xs">
            {withPrevious.map((t) => (
              <Group key={t.id} justify="space-between">
                <div>
                  <Text size="sm"><b>{t.slug}</b> — <Code>{t.previousDatabaseName}</Code></Text>
                  <Text size="xs" c="dimmed">Kept since {fmt(t.previousDatabaseAt)}. The only way back past the swap.</Text>
                </div>
                <Button
                  size="xs" variant="light" color="red"
                  onClick={async () => {
                    try { await api.discardPrevious(t.id, t.slug); void refresh(); }
                    catch (e) { setError((e as Error).message); }
                  }}
                >
                  Discard
                </Button>
              </Group>
            ))}
          </Stack>
        </Card>
      )}

      <Card withBorder padding={0}>
        {loading ? <Group p="xl" justify="center"><Loader /></Group> : (
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Tenant</Table.Th><Table.Th>Taken</Table.Th><Table.Th>Kind</Table.Th>
                <Table.Th>Size</Table.Th><Table.Th>Image</Table.Th><Table.Th>Note</Table.Th><Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {snapshots.map((s) => (
                <Table.Tr key={s.id}>
                  <Table.Td><Text fw={600}>{s.tenantSlug}</Text></Table.Td>
                  <Table.Td><Text size="sm">{fmt(s.createdAt)}</Text></Table.Td>
                  <Table.Td><Badge variant="light" color={KIND_COLOR[s.kind] ?? 'gray'}>{s.kind}</Badge></Table.Td>
                  <Table.Td>
                    {/* A row without its file is worse than no row: it reads as a
                        restore point that is not one. */}
                    {s.onDisk ? <Text size="sm">{fmtBytes(s.sizeBytes)}</Text>
                      : <Badge color="red" variant="light">file missing</Badge>}
                  </Table.Td>
                  <Table.Td><Code style={{ fontSize: 11 }}>{s.imageTag?.split('/').pop() ?? '—'}</Code></Table.Td>
                  <Table.Td><Text size="xs" c="dimmed" lineClamp={2}>{s.note}</Text></Table.Td>
                  <Table.Td>
                    <Group gap={4} justify="flex-end" wrap="nowrap">
                      <Tooltip label="Restore into a throwaway copy">
                        <ActionIcon variant="subtle" disabled={!s.onDisk} onClick={() => void restore(s)}>
                          <IconRestore size={16} />
                        </ActionIcon>
                      </Tooltip>
                      <Tooltip label="Delete this snapshot">
                        <ActionIcon variant="subtle" color="red" onClick={() => setConfirmDelete(s)}>
                          <IconTrash size={16} />
                        </ActionIcon>
                      </Tooltip>
                    </Group>
                  </Table.Td>
                </Table.Tr>
              ))}
              {snapshots.length === 0 && (
                <Table.Tr><Table.Td colSpan={7}><Text ta="center" c="dimmed" py="lg">No snapshots yet</Text></Table.Td></Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        )}
      </Card>

      <Modal opened={Boolean(takeFor)} onClose={() => setTakeFor(null)} title="Take a snapshot">
        <Stack gap="sm">
          <Text size="sm" c="dimmed">A logical dump of this tenant's database, taken now and kept on the control plane.</Text>
          <TextInput label="Note" placeholder="why you are taking it" value={note} onChange={(e) => setNote(e.currentTarget.value)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setTakeFor(null)}>Cancel</Button>
            <Button onClick={() => void take()}>Take it</Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={Boolean(confirmDelete)} onClose={() => setConfirmDelete(null)} title="Delete snapshot">
        <Stack gap="sm">
          <Alert color="red">This removes the dump file. If it is the only copy of that state, it cannot be recovered.</Alert>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setConfirmDelete(null)}>Cancel</Button>
            <Button color="red" onClick={async () => {
              const s = confirmDelete!; setConfirmDelete(null);
              try { await api.deleteSnapshot(s.id); void refresh(); } catch (e) { setError((e as Error).message); }
            }}>Delete</Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

/** One restored copy, with the two things that can be done to it. */
function RestoredCopy({ copy, tenants, onDone, onJob, onError }: {
  copy: Tenant; tenants: Tenant[];
  onDone: () => void; onJob: (id: string) => void; onError: (e: string) => void;
}) {
  const [swapping, setSwapping] = useState(false);
  const [confirm, setConfirm] = useState('');
  const target = tenants.find((t) => t.slug === copy.restoredFromSlug);

  return (
    <>
      <Group justify="space-between" wrap="nowrap">
        <div>
          <Group gap={6}>
            <Badge color="grape" variant="light">copy</Badge>
            <Anchor size="sm" fw={600} href={`https://${copy.slug}.zulo.one`} target="_blank" rel="noreferrer">
              {copy.slug}.zulo.one
            </Anchor>
          </Group>
          <Text size="xs" c="dimmed">
            Restored from <b>{copy.restoredFromSlug}</b> · {fmt(copy.restoredAt)}
          </Text>
        </div>
        <Group gap="xs" wrap="nowrap">
          <Button
            size="xs" color="grape" leftSection={<IconArrowBackUp size={14} />}
            disabled={!target} onClick={() => { setSwapping(true); setConfirm(''); }}
          >
            Swap into {copy.restoredFromSlug}
          </Button>
          <Button size="xs" variant="light" color="red" onClick={async () => {
            try { await api.remove(copy.id, copy.slug); onDone(); } catch (e) { onError((e as Error).message); }
          }}>
            Discard
          </Button>
        </Group>
      </Group>

      <Modal opened={swapping} onClose={() => setSwapping(false)} title={`Swap into ${target?.slug}`}>
        <Stack gap="sm">
          <Alert color="orange" icon={<IconAlertTriangle size={16} />}>
            <b>{target?.slug}</b> stops for a moment and comes back on this copy's data. Its slug, hostname and
            signing key do not change, so nobody is logged out.
            <br /><br />
            The database it is using now is <b>kept</b>, renamed aside — that is the undo, and it stays until you
            discard it.
          </Alert>
          <TextInput
            label={`Type "${target?.slug}" to confirm`} value={confirm}
            onChange={(e) => setConfirm(e.currentTarget.value)}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setSwapping(false)}>Cancel</Button>
            <Button color="grape" disabled={confirm !== target?.slug} onClick={async () => {
              setSwapping(false);
              try { onJob((await api.swap(target!.id, copy.id, confirm)).jobId); }
              catch (e) { onError((e as Error).message); }
            }}>
              Swap it in
            </Button>
          </Group>
        </Stack>
      </Modal>
    </>
  );
}
