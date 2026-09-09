import { useCallback, useState } from 'react';
import {
  Alert, Badge, Button, Card, Code, Group, Loader, Modal, Stack, Table, Tabs, Text, TextInput, Title, Tooltip,
} from '@mantine/core';
import { IconAlertTriangle, IconArrowUp, IconTrash } from '@tabler/icons-react';
import { api, type ImageTag, type Images, type Tenant } from './api';
import { JobProgress, useJob, usePoll } from './shared';

export function ImagesPage() {
  const [images, setImages] = useState<Images | null>(null);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [upgrade, setUpgrade] = useState<{ tag: ImageTag; tenant: Tenant } | null>(null);
  const [pick, setPick] = useState<ImageTag | null>(null);
  const [remove, setRemove] = useState<ImageTag | null>(null);
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
          {/* Two names for one image is the normal case here, not an oddity, and
              it is the whole reason the delete button needs care. */}
          {t.alsoTagged.length > 0 && (
            <Text size="xs" c="dimmed">= {t.alsoTagged.map((x) => <Code key={x} fz={11}>{x}</Code>)}</Text>
          )}
        </Group>
      </Table.Td>
      <Table.Td>
        {t.inUseBy.length === 0 ? <Text size="sm" c="dimmed">—</Text> : (
          <Group gap={4}>{t.inUseBy.map((s) => <Badge key={s} variant="light" color="green">{s}</Badge>)}</Group>
        )}
      </Table.Td>
      <Table.Td>
        <Group gap={6} justify="flex-end" wrap="nowrap">
          <Button size="xs" variant="light" leftSection={<IconArrowUp size={14} />} onClick={() => { setPick(t); setConfirm(''); }}>
            Move a tenant here
          </Button>
          <Tooltip label={t.deleteBlockedBy ?? 'Remove this image from the registry'} multiline w={320} withArrow>
            {/* A disabled button cannot fire hover, so the span carries it — and
                the reason matters more than the button here. */}
            <span>
              <Button size="xs" variant="subtle" color="red" disabled={!t.canDelete}
                leftSection={<IconTrash size={14} />}
                onClick={() => { setRemove(t); setConfirm(''); }}>
                Delete
              </Button>
            </span>
          </Tooltip>
        </Group>
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

      {/* Where releases come from, stated on the screen rather than assumed.
          The two kinds of tag look alike in a list and mean entirely different
          things, and nothing here explained which was which or how one becomes
          the other. */}
      <Card withBorder padding="md">
        <Text fw={600} mb={4}>How a build becomes a release</Text>
        <Text size="sm" c="dimmed">
          A <b>release</b> is created by tagging the platform repository{' '}
          <Code>git tag vYYYY.M.P &amp;&amp; git push --tags</Code>. CI builds that tag, asserts that{' '}
          <Code>/health</Code> reports the same version, refuses to overwrite an existing one, and publishes it.
          Release tags are immutable — a fix means a new patch, never a rebuild of the same number.
        </Text>
        <Text size="sm" c="dimmed" mt={6}>
          A <b>CI build</b> is <Code>2026.0.N</Code>. The zero is deliberate: no month is zero, so a build can never
          collide with a release. They exist to be tested, not to be pinned to a customer.
        </Text>
        <Text size="sm" c="dimmed" mt={6}>
          A CI build cannot be <i>promoted</i> into a release, and the panel deliberately offers no button for it.
          The version is compiled INTO the assembly, so re-tagging <Code>2026.0.33</Code> as <Code>2026.9.1</Code>{' '}
          would produce an image whose <Code>/health</Code> still said <Code>2026.0.33</Code> — precisely the
          mismatch CI exists to prevent. A release is rebuilt from its tag.
        </Text>
      </Card>

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
      <Modal opened={Boolean(remove)} onClose={() => setRemove(null)} title="Delete from the registry">
        <Stack gap="sm">
          {/* The registry deletes MANIFESTS, not tags. Naming only the clicked tag
              here would be a lie whenever the image has more than one name — which,
              in this registry, is nearly always. */}
          {remove && remove.alsoTagged.length > 0 ? (
            <Alert color="red" icon={<IconAlertTriangle size={16} />}>
              <Code>{remove.tag}</Code> is not a name the registry can remove on its own — it removes the image, and
              with it every name pointing at the same one. This deletes{' '}
              <b>{[remove.tag, ...remove.alsoTagged].length} tags</b>:{' '}
              {[remove.tag, ...remove.alsoTagged].map((x) => <Code key={x}>{x}</Code>).reduce((a, b) => <>{a}{', '}{b}</>)}
            </Alert>
          ) : (
            <Alert color="red" icon={<IconAlertTriangle size={16} />}>
              Removes <Code>{remove?.tag}</Code> from the registry. Nothing runs it and no release shares it.
            </Alert>
          )}
          <Text size="xs" c="dimmed">
            Disk is not freed by this. The manifest goes immediately; the layers survive until{' '}
            <Code>registry garbage-collect</Code> runs on the registry host, which the panel cannot reach.
          </Text>
          <TextInput
            label={`Type "${remove?.tag}" to confirm`} value={confirm}
            onChange={(e) => setConfirm(e.currentTarget.value)}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRemove(null)}>Cancel</Button>
            <Button color="red" disabled={confirm !== remove?.tag}
              onClick={async () => {
                const r = remove!; setRemove(null);
                try { await api.removeImage(r.tag); await refresh(); }
                catch (e) { setError((e as Error).message); }
              }}
            >
              Delete it
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
