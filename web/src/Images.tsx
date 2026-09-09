import { useCallback, useState } from 'react';
import { Link } from 'react-router-dom';
import {
  Alert, Anchor, Badge, Button, Card, Code, Group, Loader, Modal, Stack, Table, Tabs, Text, TextInput, Title,
  Tooltip,
} from '@mantine/core';
import { IconAlertTriangle, IconArrowUp, IconRocket, IconTrash } from '@tabler/icons-react';
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
  const [promote, setPromote] = useState<ImageTag | null>(null);
  // Its OWN state. `confirm` is shared by the delete and upgrade dialogs, and
  // reusing it here would put a leftover tenant slug in the version box.
  const [version, setVersion] = useState('');
  const [notes, setNotes] = useState('');
  const [released, setReleased] = useState<string | null>(null);
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

  const rows = (list: ImageTag[], isRelease: boolean) => list.map((t) => (
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
          {/* Only on builds: a release is what you promote TO, not FROM. */}
          {!isRelease && (
            <Tooltip label="Give this build a release version" withArrow>
              <Button size="xs" variant="light" color="teal" leftSection={<IconRocket size={14} />}
                      onClick={() => { setPromote(t); setVersion(''); setNotes(''); }}>
                Make a release
              </Button>
            </Tooltip>
          )}
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
          A <b>CI build</b> is <Code>2026.0.N</Code>, published on every push. The zero is deliberate:
          no month is zero, so a build can never collide with a release.
        </Text>
        <Text size="sm" c="dimmed" mt={6}>
          A <b>release</b> is a build somebody promoted — right here, with the button on its row.
          Promotion copies nothing: the release tag is a <i>second name</i> for the same manifest, so the
          bytes that were tested are the bytes that ship, with no rebuild in between. The version is the
          next patch of the current month, and it is proposed for you.
        </Text>
        <Text size="sm" c="dimmed" mt={6}>
          Two consequences, both worth knowing before you promote. The version is compiled INTO the
          assembly, so an image promoted to <Code>2026.9.5</Code> still reports its build number from{' '}
          <Code>/health</Code> — that is not a fault, it is the same image, and this screen is where the
          two names are reconciled. And because both names point at one manifest, they cannot be
          separated: deleting either removes it from under both, which is why Delete refuses whenever any
          name on a manifest is a release, is the fleet default, or is what a tenant runs.
        </Text>
      </Card>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />} withCloseButton onClose={() => setError(null)}>{error}</Alert>}
      {released && <Alert color="teal" withCloseButton onClose={() => setReleased(null)}>{released}</Alert>}
      {images?.error && <Alert color="yellow">{images.error}</Alert>}

      {defaultIsStale && (
        <Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
          The newest release is <Code>{newest?.tag}</Code>, but new tenants are created on{' '}
          <Code>{images?.defaultImage?.split(':').pop()}</Code>. The next customer would start on older code than the
          fleet already runs — change it under{' '}
          <Anchor component={Link} to="/settings">Settings → Fleet and provisioning</Anchor>. It takes effect on the
          next tenant; nothing already running is moved.
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
                <Table.Tbody>{rows(images?.releases ?? [], true)}</Table.Tbody>
              </Table>
            </Tabs.Panel>
            <Tabs.Panel value="builds">
              <Table striped highlightOnHover>
                <Table.Thead><Table.Tr><Table.Th>Tag</Table.Th><Table.Th>In use by</Table.Th><Table.Th /></Table.Tr></Table.Thead>
                <Table.Tbody>{rows(images?.builds ?? [], false)}</Table.Tbody>
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
      <Modal opened={Boolean(promote)} onClose={() => setPromote(null)} title="Make a release">
        <Stack gap="sm">
          <Alert color="teal">
            <Code>{promote?.tag}</Code> gets a release version. Nothing is rebuilt and nothing is copied —
            the release tag becomes a second name for this exact manifest, so what ships is what was tested.
          </Alert>
          {/* Left blank on purpose. The server proposes the next patch of the
              current month, and it derives that from the REGISTRY — a tag can
              exist without a release row, and the collision would be with the
              tag. Guessing it here would be a second, worse source of truth. */}
          <TextInput
            label="Version" placeholder="proposed automatically — leave empty"
            description="YYYY.M.P. Empty means the next patch of this month."
            value={version} onChange={(e) => setVersion(e.currentTarget.value)}
          />
          <TextInput
            label="What changed" placeholder="Optional, but this is the only place it gets recorded"
            value={notes} onChange={(e) => setNotes(e.currentTarget.value)}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setPromote(null)}>Cancel</Button>
            <Button color="teal" leftSection={<IconRocket size={16} />}
              onClick={async () => {
                const p = promote!; setPromote(null);
                try {
                  const r = await api.promoteImage(p.tag, version.trim() || null, notes.trim() || null);
                  setReleased(`${p.tag} is now released as ${r.version} — same digest, no rebuild.`);
                  await refresh();
                } catch (e) { setError((e as Error).message); }
              }}
            >
              Release it
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
