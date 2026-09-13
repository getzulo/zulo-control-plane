import { Fragment, useCallback, useMemo, useState } from 'react';
import {
  ActionIcon, Alert, Badge, Button, Card, Checkbox, Group, Loader, Modal, Select,
  Stack, Table, Text, TextInput, Title, Tooltip, UnstyledButton,
} from '@mantine/core';
import {
  IconAlertTriangle, IconChevronDown, IconChevronRight, IconDownload, IconHammer, IconPackage, IconRocket,
} from '@tabler/icons-react';
import { api, type ModelCatalogue } from './api';
import { JobProgress, useJob, usePoll } from './shared';

type CatalogueModel = ModelCatalogue['models'][number];
type CatalogueTenant = ModelCatalogue['tenants'][number];

function expandSelection(selected: string[], catalogue: CatalogueModel[]): string[] {
  const byName = new Map(catalogue.map((m) => [m.model.toLowerCase(), m]));
  const seen = new Set<string>();
  const queue = [...selected];
  while (queue.length > 0) {
    const name = queue.pop()!;
    const key = name.toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);
    const node = byName.get(key);
    if (!node) continue;
    for (const dep of node.dependsOn) queue.push(dep);
  }
  return catalogue.map((m) => m.model).filter((name) => seen.has(name.toLowerCase()));
}

function stillRequired(name: string, selected: string[], catalogue: CatalogueModel[]): string[] {
  const others = selected.filter((s) => s.toLowerCase() !== name.toLowerCase());
  return others.filter((s) => expandSelection([s], catalogue).some((x) => x.toLowerCase() === name.toLowerCase()));
}

export function ModelsPage() {
  const [data, setData] = useState<ModelCatalogue | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [selected, setSelected] = useState<string[]>([]);
  const [openTenant, setOpenTenant] = useState<string | null>(null);
  const [planning, setPlanning] = useState(false);
  const [targetImage, setTargetImage] = useState<string | null>(null);
  const [confirm, setConfirm] = useState('');
  const [jobId, setJobId] = useState<string | null>(null);
  const [installing, setInstalling] = useState<CatalogueTenant | null>(null);
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

  usePoll(refresh, 60_000);

  const models = data?.models ?? [];
  const tenants = data?.tenants ?? [];
  const installable = useMemo(() => models.filter((m) => !m.isSystem), [models]);
  const imageOptions = useMemo(
    () => (data?.images ?? []).map((i) => ({ value: i.image, label: `${i.image}  (${i.models.length} models)` })),
    [data]);
  const outdatedTenants = tenants.filter((t) => t.outdatedCount > 0 || t.missingCount > 0).length;

  function openInstall(tenant: CatalogueTenant, preset?: string[]) {
    const source = tenant.sourceImage ?? data?.sourceImage ?? null;
    setInstalling(tenant);
    setInstallImage(source);
    const chosen = preset ?? tenant.installed.filter((m) => m.outdated && !m.isSystem).map((m) => m.name);
    setInstallModels(expandSelection(chosen, models));
  }

  function toggleInstallModel(name: string, checked: boolean) {
    if (checked) {
      setInstallModels(expandSelection([...installModels, name], models));
      return;
    }
    const requiredBy = stillRequired(name, installModels, models);
    if (requiredBy.length > 0) return;
    setInstallModels(expandSelection(installModels.filter((m) => m.toLowerCase() !== name.toLowerCase()), models));
  }

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
        setModels: false,
        models: null,
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
            What each tenant has, what is outdated, and what installing one model also pulls in.
          </Text>
        </div>
        <Group gap="sm">
          {outdatedTenants > 0 && (
            <Badge color="yellow" variant="light">{outdatedTenants} tenant{outdatedTenants === 1 ? '' : 's'} outdated</Badge>
          )}
          {data?.sourceImage && (
            <Tooltip label="Installs read the model tree from this distribution image">
              <Badge variant="light">{data.sourceImage}</Badge>
            </Tooltip>
          )}
          <Button
            leftSection={<IconRocket size={15} />}
            disabled={selected.length === 0}
            onClick={() => setPlanning(true)}>
            Roll out image to {selected.length || 'none'}
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
          <Text fw={600} size="sm">Tenants ({tenants.length})</Text>
        </Group>
        {loading ? (
          <Group p="xl" justify="center"><Loader /></Group>
        ) : (
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th w={36} />
                <Table.Th w={36} />
                <Table.Th>Tenant</Table.Th>
                <Table.Th>Image</Table.Th>
                <Table.Th>Installed</Table.Th>
                <Table.Th>Outdated</Table.Th>
                <Table.Th>Compile</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {tenants.map((t) => {
                const open = openTenant === t.id;
                return (
                  <Fragment key={t.id}>
                    <Table.Tr>
                      <Table.Td>
                        <UnstyledButton onClick={() => setOpenTenant(open ? null : t.id)} aria-label={`Toggle ${t.slug}`}>
                          {open ? <IconChevronDown size={16} /> : <IconChevronRight size={16} />}
                        </UnstyledButton>
                      </Table.Td>
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
                        <Text size="sm">{t.installed.filter((m) => !m.isSystem).length}</Text>
                      </Table.Td>
                      <Table.Td>
                        {t.outdatedCount === 0 && t.missingCount === 0
                          ? <Text size="xs" c="dimmed">up to date</Text>
                          : (
                            <Group gap={6}>
                              {t.outdatedCount > 0 && (
                                <Badge color="yellow" variant="light" size="sm">{t.outdatedCount} behind</Badge>
                              )}
                              {t.missingCount > 0 && (
                                <Tooltip label={t.missing.map((m) => `${m.name} ${m.version}`).join(', ')}>
                                  <Badge color="gray" variant="light" size="sm">{t.missingCount} not installed</Badge>
                                </Tooltip>
                              )}
                            </Group>
                          )}
                      </Table.Td>
                      <Table.Td>
                        {t.brokenCount === 0
                          ? <Text size="xs" c="dimmed">ok</Text>
                          : (
                            <Tooltip label={t.installed.filter((m) => !m.compiles).map((m) => `${m.name}: ${m.compilationError ?? m.compilationStatus}`).join('\n')}>
                              <Badge color="red" variant="light" size="sm">{t.brokenCount} failed</Badge>
                            </Tooltip>
                          )}
                      </Table.Td>
                      <Table.Td>
                        <Group gap={4} wrap="nowrap">
                          <Tooltip label="Generate tables and types, then compile scripts — no install">
                            <ActionIcon variant="subtle" onClick={async () => {
                              try { setJobId((await api.compileModels(t.id)).jobId); }
                              catch (e) { setError((e as Error).message); }
                            }}>
                              <IconHammer size={16} />
                            </ActionIcon>
                          </Tooltip>
                          <Tooltip label="Install or update models while this tenant keeps serving">
                            <ActionIcon variant="subtle" onClick={() => openInstall(t)}>
                              <IconDownload size={16} />
                            </ActionIcon>
                          </Tooltip>
                        </Group>
                      </Table.Td>
                    </Table.Tr>
                    {open && (
                      <Table.Tr className="zo-models-detail">
                        <Table.Td colSpan={8} p={0}>
                          <TenantModelsDetail tenant={t} catalogue={models} onInstall={openInstall} />
                        </Table.Td>
                      </Table.Tr>
                    )}
                  </Fragment>
                );
              })}
              {tenants.length === 0 && (
                <Table.Tr>
                  <Table.Td colSpan={8}>
                    <Text ta="center" c="dimmed" py="lg">No tenants yet</Text>
                  </Table.Td>
                </Table.Tr>
              )}
            </Table.Tbody>
          </Table>
        )}
      </Card>

      <Card withBorder padding={0}>
        <Group p="sm" gap="xs">
          <IconPackage size={16} />
          <Text fw={600} size="sm">Catalogue ({installable.length})</Text>
        </Group>
        {loading ? (
          <Group p="xl" justify="center"><Loader /></Group>
        ) : (
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Model</Table.Th>
                <Table.Th>Latest</Table.Th>
                <Table.Th>Depends on</Table.Th>
                <Table.Th>Tenants</Table.Th>
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {installable.map((m) => {
                const holders = tenants.filter((t) =>
                  t.installed.some((i) => i.name.toLowerCase() === m.model.toLowerCase()));
                const behind = holders.filter((t) =>
                  t.installed.some((i) => i.name.toLowerCase() === m.model.toLowerCase() && i.outdated));
                return (
                  <Table.Tr key={m.model}>
                    <Table.Td><Text size="sm" fw={500}>{m.model}</Text></Table.Td>
                    <Table.Td><Badge variant="light" size="sm">{m.latest}</Badge></Table.Td>
                    <Table.Td>
                      {m.dependsOn.length === 0
                        ? <Text size="xs" c="dimmed">—</Text>
                        : <Text size="xs" c="dimmed">{m.dependsOn.join(', ')}</Text>}
                    </Table.Td>
                    <Table.Td>
                      <Group gap={6}>
                        <Text size="sm">{holders.length} installed</Text>
                        {behind.length > 0 && (
                          <Tooltip label={behind.map((t) => t.slug).join(', ')}>
                            <Badge color="yellow" variant="light" size="sm">{behind.length} outdated</Badge>
                          </Tooltip>
                        )}
                      </Group>
                    </Table.Td>
                  </Table.Tr>
                );
              })}
              {installable.length === 0 && (
                <Table.Tr>
                  <Table.Td colSpan={4}>
                    <Text ta="center" c="dimmed" py="lg">
                      No distribution image in the registry declares a model set.
                    </Text>
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
            The container stays up. A snapshot is taken first — that file is the only
            undo, because the image does not change. Checking one model also checks
            everything it depends on.
          </Alert>

          <Select
            label="Take the models from"
            description="A distribution image. zuloone-core carries none."
            placeholder="(pick a distribution image)"
            data={imageOptions}
            value={installImage}
            onChange={setInstallImage}
            searchable
          />

          <Stack gap={6}>
            <Group justify="space-between">
              <Text size="sm" fw={500}>Models</Text>
              <Button
                size="compact-xs"
                variant="subtle"
                onClick={() => setInstallModels(expandSelection(installable.map((m) => m.model), models))}>
                Select all
              </Button>
            </Group>
            {installable.map((m) => {
              const checked = installModels.some((s) => s.toLowerCase() === m.model.toLowerCase());
              const requiredBy = checked ? stillRequired(m.model, installModels, models) : [];
              const onTenant = installing?.installed.some((i) => i.name.toLowerCase() === m.model.toLowerCase());
              const row = installing?.installed.find((i) => i.name.toLowerCase() === m.model.toLowerCase());
              return (
                <Checkbox
                  key={m.model}
                  label={
                    <Group gap={8} wrap="wrap">
                      <Text size="sm">{m.model}</Text>
                      <Text size="xs" c="dimmed">{m.latest}</Text>
                      {row?.outdated && <Badge size="xs" color="yellow">outdated {row.version ?? '?'}</Badge>}
                      {!onTenant && <Badge size="xs" color="gray">not installed</Badge>}
                      {m.dependsOn.length > 0 && (
                        <Text size="xs" c="dimmed">depends on {m.dependsOn.join(', ')}</Text>
                      )}
                      {(m.extends ?? []).length > 0 && (
                        <Text size="xs" c="dimmed">extends {(m.extends ?? []).join(', ')}</Text>
                      )}
                      {requiredBy.length > 0 && (
                        <Text size="xs" c="dimmed">locked — {requiredBy.join(', ')} need this</Text>
                      )}
                    </Group>
                  }
                  checked={checked}
                  disabled={requiredBy.length > 0}
                  onChange={(e) => toggleInstallModel(m.model, e.currentTarget.checked)}
                />
              );
            })}
          </Stack>

          <Group justify="flex-end">
            <Button variant="default" onClick={() => setInstalling(null)}>Cancel</Button>
            <Button color="grape" disabled={installModels.length === 0} onClick={() => void install()}>
              Install {installModels.length} model{installModels.length === 1 ? '' : 's'}
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={planning} onClose={() => setPlanning(false)} title="Roll out image" size="lg">
        <Stack gap="md">
          <Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
            Recreates each selected tenant on the new image, one at a time. To add or
            update models on a running tenant, use Install on that row instead.
          </Alert>
          <Select
            label="Move to image"
            data={imageOptions}
            value={targetImage}
            onChange={setTargetImage}
            searchable
          />
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
            <Button color="grape" disabled={confirm !== 'rollout' || !targetImage} onClick={() => void start()}>
              Start the wave
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}

function TenantModelsDetail({
  tenant,
  catalogue,
  onInstall,
}: {
  tenant: CatalogueTenant;
  catalogue: CatalogueModel[];
  onInstall: (tenant: CatalogueTenant, preset?: string[]) => void;
}) {
  const rows = tenant.installed.filter((m) => !m.isSystem);
  return (
    <Stack gap="sm" p="sm">
      <Group gap="xs">
        {tenant.outdatedCount > 0 && (
          <Button size="xs" variant="light" color="yellow" onClick={() => onInstall(tenant)}>
            Update {tenant.outdatedCount} outdated
          </Button>
        )}
        {tenant.missing.length > 0 && (
          <Button
            size="xs"
            variant="light"
            onClick={() => onInstall(tenant, tenant.missing.map((m) => m.name))}>
            Add missing
          </Button>
        )}
        <Button size="xs" variant="default" onClick={() => onInstall(tenant, [])}>
          Choose models…
        </Button>
      </Group>
      <Table fz="sm" withRowBorders={false} withTableBorder={false} className="zo-models-detail-table">
        <Table.Thead>
          <Table.Tr>
            <Table.Th>Model</Table.Th>
            <Table.Th>Installed</Table.Th>
            <Table.Th>Latest</Table.Th>
            <Table.Th>Depends on</Table.Th>
            <Table.Th>Compile</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rows.map((m) => {
            const deps = catalogue.find((c) => c.model.toLowerCase() === m.name.toLowerCase())?.dependsOn ?? [];
            return (
              <Table.Tr key={m.name}>
                <Table.Td>
                  <UnstyledButton onClick={() => onInstall(tenant, [m.name])}>
                    <Text size="sm" fw={500}>{m.name}</Text>
                  </UnstyledButton>
                </Table.Td>
                <Table.Td>{m.version ?? '—'}</Table.Td>
                <Table.Td>
                  <Group gap={6}>
                    <Text size="sm">{m.latest ?? '—'}</Text>
                    {m.outdated && <Badge size="xs" color="yellow">outdated</Badge>}
                  </Group>
                </Table.Td>
                <Table.Td><Text size="xs" c="dimmed">{deps.join(', ') || '—'}</Text></Table.Td>
                <Table.Td>
                  {m.compilationStatus
                    ? <Tooltip label={m.compilationError ?? m.compilationStatus} disabled={m.compiles}>
                        <Badge size="xs" color={m.compiles ? 'green' : 'red'}>{m.compilationStatus}</Badge>
                      </Tooltip>
                    : <Text size="xs" c="dimmed">—</Text>}
                </Table.Td>
              </Table.Tr>
            );
          })}
          {rows.length === 0 && (
            <Table.Tr>
              <Table.Td colSpan={5}>
                <Text size="sm" c="dimmed">No business models installed.</Text>
              </Table.Td>
            </Table.Tr>
          )}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}
