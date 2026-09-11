import { useCallback, useMemo, useState } from 'react';
import {
  ActionIcon, Alert, Badge, Button, Card, Checkbox, Group, Loader, Modal, MultiSelect, Select,
  Stack, Table, Text, TextInput, Title, Tooltip,
} from '@mantine/core';
import { IconAlertTriangle, IconDownload, IconPackage, IconRocket } from '@tabler/icons-react';
import { api, type ModelCatalogue } from './api';
import { JobProgress, useJob, usePoll } from './shared';

/**
 * What models exist, at what versions, and what each tenant is actually running.
 *
 * The two halves come from different places on purpose. The catalogue is read from
 * the REGISTRY's image labels, so it covers images nobody has pulled — which is
 * exactly the set an operator is about to roll out. The tenant rows are read from
 * each tenant's OWN database, because the registry records only the tag a tenant is
 * pinned to and never what that tag installed.
 *
 * Where the two disagree is the interesting part, and it has been wrong in both
 * directions: a tenant on an image carrying no business layer at all, and a package
 * row claiming a version whose content never arrived.
 */
export function ModelsPage() {
  const [data, setData] = useState<ModelCatalogue | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [selected, setSelected] = useState<string[]>([]);
  const [planning, setPlanning] = useState(false);
  const [targetImage, setTargetImage] = useState<string | null>(null);
  const [targetModels, setTargetModels] = useState<string[]>([]);
  const [changeModels, setChangeModels] = useState(false);
  const [confirm, setConfirm] = useState('');
  const [jobId, setJobId] = useState<string | null>(null);
  const [installing, setInstalling] = useState<{ id: string; slug: string } | null>(null);
  const [installImage, setInstallImage] = useState<string | null>(null);
  const [installModels, setInstallModels] = useState<string[]>([]);
  const job = useJob(jobId);

  const refresh = useCallback(async () => {
    try {
      setData(await api.modelCatalogue());
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }, []);

  // Slower than the fleet screens: the registry does not change on its own, and each
  // refresh reads every tenant's database.
  usePoll(refresh, 60_000);

  const models = data?.models ?? [];
  const tenants = data?.tenants ?? [];
  const imageOptions = useMemo(
    () => (data?.images ?? []).map((i) => ({ value: i.image, label: `${i.image}  (${i.models.length} models)` })),
    [data]);
  const modelOptions = useMemo(() => models.map((m) => m.model), [models]);

  async function install() {
    if (!installing) return;
    try {
      const r = await api.installModels(installing.id, {
        imageTag: installImage,
        models: installModels,
      });
      setJobId(r.jobId);
      setInstalling(null);
    } catch (e) {
      setError((e as Error).message);
    }
  }

  async function start() {
    try {
      const r = await api.startRollout({
        imageTag: targetImage,
        setModels: changeModels,
        models: changeModels ? targetModels : null,
        tenantIds: selected,
      });
      setJobId(r.jobId);
      setPlanning(false);
      setConfirm('');
    } catch (e) {
      setError((e as Error).message);
    }
  }

  return (
    <Stack gap="lg">
      <Group justify="space-between" align="flex-end">
        <div>
          <Title order={3}>Models</Title>
          <Text size="sm" c="dimmed">
            What the registry can install, and what each tenant is running.
          </Text>
        </div>
        <Group gap="sm">
          {data?.registry && <Badge variant="light">{data.registry}</Badge>}
          <Button
            leftSection={<IconRocket size={15} />}
            disabled={selected.length === 0}
            onClick={() => setPlanning(true)}>
            Roll out to {selected.length || 'none'}
          </Button>
        </Group>
      </Group>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />}>{error}</Alert>}

      {job && (
        <Card withBorder padding="sm">
          <Group justify="space-between" align="flex-start">
            <div style={{ flex: 1 }}><JobProgress job={job} /></div>
            <Group gap="xs">
              {!['Succeeded', 'Failed', 'Cancelled'].includes(job.state) && (
                <Button size="xs" variant="default" onClick={() => void api.cancelJob(job.id)}>
                  Stop after this tenant
                </Button>
              )}
              <Button size="xs" variant="subtle" onClick={() => { setJobId(null); void refresh(); }}>
                Dismiss
              </Button>
            </Group>
          </Group>
        </Card>
      )}
      {data?.error && <Alert color="yellow" icon={<IconAlertTriangle size={16} />}>{data.error}</Alert>}

      <Card withBorder padding={0}>
        <Group p="sm" gap="xs">
          <IconPackage size={16} />
          <Text fw={600} size="sm">Available ({models.length})</Text>
        </Group>
        {loading ? (
          <Group p="xl" justify="center"><Loader /></Group>
        ) : (
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Model</Table.Th>
                <Table.Th>Versions</Table.Th>
                <Table.Th>Carried by</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {models.map((m) => (
                <Table.Tr key={m.model}>
                  <Table.Td><Text size="sm" fw={500}>{m.model}</Text></Table.Td>
                  <Table.Td>
                    <Group gap={6}>
                      {m.versions.map((v) => (
                        <Badge key={v.version} variant="light" size="sm">{v.version}</Badge>
                      ))}
                    </Group>
                  </Table.Td>
                  <Table.Td>
                    <Text size="xs" c="dimmed">
                      {m.versions.flatMap((v) => v.images).join(', ')}
                    </Text>
                  </Table.Td>
                </Table.Tr>
              ))}
              {models.length === 0 && (
                <Table.Tr>
                  <Table.Td colSpan={3}>
                    <Text ta="center" c="dimmed" py="lg">
                      No image in the registry declares a model set. A distribution image
                      carries one; a platform-only image does not.
                    </Text>
                  </Table.Td>
                </Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        )}
      </Card>

      <Card withBorder padding={0}>
        <Group p="sm" gap="xs">
          <Text fw={600} size="sm">Per tenant ({tenants.length})</Text>
        </Group>
        {loading ? (
          <Group p="xl" justify="center"><Loader /></Group>
        ) : (
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th w={40} />
                <Table.Th>Tenant</Table.Th>
                <Table.Th>Image</Table.Th>
                <Table.Th>Installed</Table.Th>
                <Table.Th>Behind</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {tenants.map((t) => {
                // "Behind" is the honest diff: the image offers a version the database
                // does not hold. Models the image says nothing about are not behind —
                // they are simply not its business.
                const behind = t.installed.filter((m) => m.offers && m.offers !== m.version);
                const broken = t.installed.filter(
                  (m) => m.compilationStatus && m.compilationStatus !== 'Success');
                return (
                  <Table.Tr key={t.id}>
                    <Table.Td>
                      <Checkbox
                        aria-label={`Include ${t.slug} in the rollout`}
                        checked={selected.includes(t.id)}
                        onChange={(e) => setSelected((s) =>
                          e.currentTarget.checked ? [...s, t.id] : s.filter((x) => x !== t.id))}
                      />
                    </Table.Td>
                    <Table.Td>
                      <Text size="sm" fw={500}>{t.slug}</Text>
                      {t.error && <Text size="xs" c="red">{t.error}</Text>}
                    </Table.Td>
                    <Table.Td><Text size="xs" c="dimmed">{t.imageTag}</Text></Table.Td>
                    <Table.Td>
                      <Group gap={6}>
                        <Text size="sm">{t.installed.length}</Text>
                        {t.carries === null && (
                          <Tooltip label="This image's labels could not be read, so there is nothing to compare against.">
                            <Badge color="gray" variant="light" size="sm">image unknown</Badge>
                          </Tooltip>
                        )}
                        {t.carries?.length === 0 && (
                          <Tooltip label="A platform-only image: it carries no business layer.">
                            <Badge color="gray" variant="light" size="sm">no models in image</Badge>
                          </Tooltip>
                        )}
                        {broken.length > 0 && (
                          <Tooltip label={broken.map((m) => `${m.name}: ${m.compilationError ?? m.compilationStatus}`).join('\n')}>
                            <Badge color="red" variant="light" size="sm">{broken.length} not compiling</Badge>
                          </Tooltip>
                        )}
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <Tooltip label="Install models into this tenant while it runs — no container recreate">
                        <ActionIcon
                          variant="subtle"
                          onClick={() => {
                            setInstalling({ id: t.id, slug: t.slug });
                            setInstallImage(null);
                            setInstallModels(t.installed.filter((m) => !m.isSystem).map((m) => m.name));
                          }}>
                          <IconDownload size={16} />
                        </ActionIcon>
                      </Tooltip>
                    </Table.Td>
                    <Table.Td>
                      {behind.length === 0
                        ? <Text size="xs" c="dimmed">—</Text>
                        : (
                          <Tooltip label={behind.map((m) => `${m.name}: ${m.version ?? '—'} → ${m.offers}`).join('\n')}>
                            <Badge color="yellow" variant="light" size="sm">{behind.length}</Badge>
                          </Tooltip>
                        )}
                    </Table.Td>
                  </Table.Tr>
                );
              })}
              {tenants.length === 0 && (
                <Table.Tr>
                  <Table.Td colSpan={6}>
                    <Text ta="center" c="dimmed" py="lg">No tenants yet</Text>
                  </Table.Td>
                </Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        )}
      </Card>
      <Modal
        opened={Boolean(installing)}
        onClose={() => setInstalling(null)}
        title={`Install models into ${installing?.slug ?? ''}`}
        size="lg">
        <Stack gap="md">
          <Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
            The container is not recreated — the models go into the tenant while it
            serves. A snapshot is taken first and it is the ONLY way back: there is no
            previous image to pin. The install does schema work and a compile, so it is
            minutes of load on a live tenant, not an instant switch.
          </Alert>

          <Select
            label="Take the models from"
            description="A distribution image. The tenant's own image is used when this is empty — a platform-only image carries none."
            placeholder="(the tenant's current image)"
            data={imageOptions}
            value={installImage}
            onChange={setInstallImage}
            clearable
            searchable
          />

          <MultiSelect
            label="Models"
            description="Empty installs everything the image carries. Installing a subset can leave a model unable to compile if the dependency graph does not declare what it really needs."
            data={modelOptions}
            value={installModels}
            onChange={setInstallModels}
            searchable
          />

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setInstalling(null)}>Cancel</Button>
            <Button color="grape" onClick={() => void install()}>Install now</Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={planning} onClose={() => setPlanning(false)} title="Roll out" size="lg">
        <Stack gap="md">
          <Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
            Each tenant is snapshotted, recreated and health-checked before the next one
            starts. The wave stops at the first that does not come up — the ones already
            moved stay moved.
          </Alert>

          <Select
            label="Move to image"
            description="Leave empty to keep each tenant on the image it already runs."
            placeholder="(no change)"
            data={imageOptions}
            value={targetImage}
            onChange={setTargetImage}
            clearable
            searchable
          />

          <Checkbox
            label="Also change which models they install"
            checked={changeModels}
            onChange={(e) => setChangeModels(e.currentTarget.checked)}
          />
          {changeModels && (
            <MultiSelect
              label="Models"
              description="Empty means this tenant installs nothing. Clear the box above to leave the pin alone."
              data={modelOptions}
              value={targetModels}
              onChange={setTargetModels}
              searchable
            />
          )}

          <Text size="sm">
            {selected.length} tenant(s):{' '}
            {tenants.filter((t) => selected.includes(t.id)).map((t) => t.slug).join(', ')}
          </Text>

          <TextInput
            label={'Type "rollout" to confirm'}
            value={confirm}
            onChange={(e) => setConfirm(e.currentTarget.value)}
          />

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setPlanning(false)}>Cancel</Button>
            <Button
              color="grape"
              disabled={confirm !== 'rollout' || (!targetImage && !changeModels)}
              onClick={() => void start()}>
              Start the wave
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
