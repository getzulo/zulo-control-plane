import { useCallback, useState } from 'react';
import {
  Alert, Badge, Button, Card, Code, Group, Loader, Modal, Stack, Table, Tabs, Text, TextInput, Title,
} from '@mantine/core';
import { IconAlertTriangle, IconArrowUp } from '@tabler/icons-react';
import { api, type ImageTag, type Images, type Tenant } from './api';
import { JobProgress, useJob, usePoll } from './shared';

export function ImagesPage() {
  const [images, setImages] = useState<Images | null>(null);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [upgrade, setUpgrade] = useState<{ tag: ImageTag; tenant: Tenant } | null>(null);
  const [pick, setPick] = useState<ImageTag | null>(null);
  const [confirm, setConfirm] = useState('');
  const [jobId, setJobId] = useState<string | null>(null);
  const job = useJob(jobId);

  const refresh = useCallback(async () => {
    try {
      const [i, t] = await Promise.all([api.images(), api.listTenants()]);
      setImages(i); setTenants(t); setError(null);
    } catch (e) { setError((e as Error).message); }
    finally { setLoading(false); }
  }, []);

  usePoll(refresh, 20_000);

  const real = tenants.filter((t) => !t.restoredFromSlug);
  // The drift worth naming: a new tenant starts on the default image, so a default
  // older than what the fleet runs means the newest customer gets the oldest code.
  const newest = images?.releases[0];
  const defaultIsStale = Boolean(newest && images?.defaultImage && newest.image !== images.defaultImage);

  const rows = (list: ImageTag[]) => list.map((t) => (
    <Table.Tr key={t.tag}>
      <Table.Td>
        <Group gap={6}>
          <Code>{t.tag}</Code>
          {t.isDefault && <Badge size="xs" variant="light">default for new tenants</Badge>}
        </Group>
      </Table.Td>
      <Table.Td>
        {t.inUseBy.length === 0 ? <Text size="sm" c="dimmed">—</Text> : (
          <Group gap={4}>{t.inUseBy.map((s) => <Badge key={s} variant="light" color="green">{s}</Badge>)}</Group>
        )}
      </Table.Td>
      <Table.Td>
        <Button size="xs" variant="light" leftSection={<IconArrowUp size={14} />} onClick={() => { setPick(t); setConfirm(''); }}>
          Move a tenant here
        </Button>
      </Table.Td>
    </Table.Tr>
  ));

  return (
    <Stack gap="md">
      <div>
        <Title order={3}>Images</Title>
        <Text size="sm" c="dimmed">
          What the registry holds, who runs what, and moving a tenant between them
        </Text>
      </div>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />} withCloseButton onClose={() => setError(null)}>{error}</Alert>}
      {images?.error && <Alert color="yellow">{images.error}</Alert>}

      {defaultIsStale && (
        <Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
          The newest release is <Code>{newest?.tag}</Code>, but new tenants are created on{' '}
          <Code>{images?.defaultImage?.split(':').pop()}</Code>. The next customer would start on older code than the
          fleet already runs — change <Code>Fleet__DefaultImage</Code>.
        </Alert>
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
          <Tabs defaultValue="releases">
            <Tabs.List>
              <Tabs.Tab value="releases">Releases ({images?.releases.length ?? 0})</Tabs.Tab>
              {/* Separate, not interleaved: a CI build is not something to pin a
                  customer to, and mixing them makes the list unreadable. */}
              <Tabs.Tab value="builds">CI builds ({images?.builds.length ?? 0})</Tabs.Tab>
            </Tabs.List>
            <Tabs.Panel value="releases">
              <Table striped highlightOnHover>
                <Table.Thead><Table.Tr><Table.Th>Tag</Table.Th><Table.Th>In use by</Table.Th><Table.Th /></Table.Tr></Table.Thead>
                <Table.Tbody>{rows(images?.releases ?? [])}</Table.Tbody>
              </Table>
            </Tabs.Panel>
            <Tabs.Panel value="builds">
              <Table striped highlightOnHover>
                <Table.Thead><Table.Tr><Table.Th>Tag</Table.Th><Table.Th>In use by</Table.Th><Table.Th /></Table.Tr></Table.Thead>
                <Table.Tbody>{rows(images?.builds ?? [])}</Table.Tbody>
              </Table>
            </Tabs.Panel>
          </Tabs>
        )}
      </Card>

      {/* Pick the tenant for the chosen tag. */}
      <Modal opened={Boolean(pick) && !upgrade} onClose={() => setPick(null)} title={`Move a tenant to ${pick?.tag}`}>
        <Stack gap="xs">
          {real.filter((t) => t.imageTag !== pick?.image).map((t) => (
            <Group key={t.id} justify="space-between">
              <div>
                <Text fw={600} size="sm">{t.slug}</Text>
                <Text size="xs" c="dimmed">now on {t.imageTag.split(':').pop()}</Text>
              </div>
              <Button size="xs" onClick={() => setUpgrade({ tag: pick!, tenant: t })}>Choose</Button>
            </Group>
          ))}
          {real.filter((t) => t.imageTag !== pick?.image).length === 0 && (
            <Text c="dimmed" size="sm">Every tenant is already on this image.</Text>
          )}
        </Stack>
      </Modal>

      <Modal opened={Boolean(upgrade)} onClose={() => setUpgrade(null)} title="Upgrade tenant">
        <Stack gap="sm">
          <Alert color="orange" icon={<IconAlertTriangle size={16} />}>
            <b>{upgrade?.tenant.slug}</b> moves from <Code>{upgrade?.tenant.imageTag.split(':').pop()}</Code> to{' '}
            <Code>{upgrade?.tag.tag}</Code>. A snapshot is taken first and it IS the rollback — migrations only run
            forward, so nothing else can undo one.
            <br /><br />
            The tenant is unavailable while it boots: migrations, schema sync and a metadata compile run against its
            real data. If the new image will not start, the old tag is pinned back automatically.
          </Alert>
          <TextInput
            label={`Type "${upgrade?.tenant.slug}" to confirm`} value={confirm}
            onChange={(e) => setConfirm(e.currentTarget.value)}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setUpgrade(null)}>Cancel</Button>
            <Button
              color="orange" disabled={confirm !== upgrade?.tenant.slug}
              onClick={async () => {
                const u = upgrade!; setUpgrade(null); setPick(null);
                try { setJobId((await api.upgrade(u.tenant.id, u.tag.image)).jobId); }
                catch (e) { setError((e as Error).message); }
              }}
            >
              Snapshot and upgrade
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
