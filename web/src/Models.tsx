import { Fragment, useCallback, useMemo, useState } from 'react';
import {
  ActionIcon, Alert, Badge, Button, Card, Checkbox, Code, Group, Loader, Modal, Select,
  Stack, Table, Text, TextInput, Title, Tooltip, UnstyledButton,
} from '@mantine/core';
import {
  IconAlertTriangle, IconChevronDown, IconChevronRight, IconDownload, IconHammer, IconPackage, IconRocket, IconTrash,
} from '@tabler/icons-react';
import { api, type ModelCatalogue } from './api';
import { JobProgress, inputChecked, useJob, usePoll } from './shared';

type CatalogueModel = ModelCatalogue['models'][number];
type CatalogueTenant = ModelCatalogue['tenants'][number];
type CatalogueImage = ModelCatalogue['images'][number];

function cmpVer(a?: string | null, b?: string | null): number {
  const parse = (v?: string | null) =>
    (v ?? '0').split('.').map((p) => parseInt(p.replace(/\D/g, ''), 10) || 0);
  const pa = parse(a);
  const pb = parse(b);
  const n = Math.max(pa.length, pb.length);
  for (let i = 0; i < n; i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0);
    if (d) return d;
  }
  return 0;
}

function tagOf(image: string): string {
  const i = image.lastIndexOf(':');
  return i >= 0 ? image.slice(i + 1) : image;
}

function newestImage(images: string[]): string | null {
  if (images.length === 0) return null;
  return [...images].sort((a, b) => cmpVer(tagOf(b), tagOf(a)))[0] ?? null;
}

function versionInPack(pack: CatalogueImage | undefined, model: string): string | undefined {
  return pack?.models.find((m) => m.name.toLowerCase() === model.toLowerCase())?.version;
}

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

function installedDependents(name: string, tenant: CatalogueTenant, catalogue: CatalogueModel[]): string[] {
  const installed = new Set(
    tenant.installed.filter((m) => !m.isSystem).map((m) => m.name.toLowerCase()),
  );
  return catalogue
    .filter((c) =>
      !c.isSystem
      && c.dependsOn.some((d) => d.toLowerCase() === name.toLowerCase())
      && installed.has(c.model.toLowerCase()))
    .map((c) => c.model);
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
  const [dropPackConfirm, setDropPackConfirm] = useState('');
  const [droppingPack, setDroppingPack] = useState(false);
  const [removingPack, setRemovingPack] = useState<CatalogueImage | null>(null);
  const [removing, setRemoving] = useState<{ tenant: CatalogueTenant; model: string } | null>(null);
  const [removeConfirm, setRemoveConfirm] = useState('');
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
  const packs = useMemo(
    () => [...(data?.images ?? [])].sort((a, b) => cmpVer(b.tag, a.tag)),
    [data]);
  const selectedPack = packs.find((p) => p.image === installImage);
  const packInUse = (pack?: CatalogueImage) =>
    tenants.filter((t) => t.imageTag === pack?.image).map((t) => t.slug);
  const imageOptions = useMemo(
    () => packs.map((i) => ({
      value: i.image,
      label: `${i.tag}  ·  ${i.models.length} models`,
    })),
    [packs]);
  const outdatedTenants = tenants.filter((t) => t.outdatedCount > 0 || t.missingCount > 0).length;

  function openInstall(tenant: CatalogueTenant, preset?: string[]) {
    const source = tenant.sourceImage ?? data?.sourceImage ?? packs[0]?.image ?? null;
    setInstalling(tenant);
    setInstallImage(source);
    const chosen = preset ?? tenant.installed.filter((m) => m.outdated && !m.isSystem).map((m) => m.name);
    setInstallModels(expandSelection(chosen, models));
    setDropPackConfirm('');
  }

  function switchPack(image: string | null) {
    setInstallImage(image);
    setDropPackConfirm('');
    const pack = packs.find((p) => p.image === image);
    const names = new Set((pack?.models ?? []).map((m) => m.name.toLowerCase()));
    setInstallModels((prev) => expandSelection(prev.filter((n) => names.has(n.toLowerCase())), models));
  }

  function pickModelVersion(model: string, version: string) {
    const entry = models.find((m) => m.model.toLowerCase() === model.toLowerCase())
      ?.versions.find((v) => v.version === version);
    if (!entry) return;
    const next = (installImage && entry.images.includes(installImage))
      ? installImage
      : newestImage(entry.images);
    setInstallImage(next);
    setDropPackConfirm('');
    const pack = packs.find((p) => p.image === next);
    const names = new Set((pack?.models ?? []).map((m) => m.name.toLowerCase()));
    setInstallModels((prev) => {
      const kept = prev.filter((n) => names.has(n.toLowerCase()));
      if (!kept.some((n) => n.toLowerCase() === model.toLowerCase())) kept.push(model);
      return expandSelection(kept, models);
    });
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

  async function dropPack(pack: CatalogueImage) {
    setDroppingPack(true);
    try {
      await api.removeImage(pack.tag);
      if (installImage === pack.image) {
        const remaining = packs.filter((p) => p.image !== pack.image);
        setInstallImage(remaining[0]?.image ?? null);
      }
      setDropPackConfirm('');
      setRemovingPack(null);
      await refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setDroppingPack(false);
    }
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

  async function uninstall() {
    if (!removing) return;
    try {
      const r = await api.uninstallModels(removing.tenant.id, removing.model);
      setJobId(r.jobId);
      setRemoving(null);
      setRemoveConfirm('');
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
          {packs.length > 0 && (
            <Tooltip label="Each distribution image is one consistent pack of model versions">
              <Badge variant="light">{packs.length} pack{packs.length === 1 ? '' : 's'}</Badge>
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
                          onChange={(e) => {
                            const on = inputChecked(e);
                            setSelected((s) => on ? [...s, t.id] : s.filter((x) => x !== t.id));
                          }}
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
                          <TenantModelsDetail
                            tenant={t}
                            catalogue={models}
                            onInstall={openInstall}
                            onRemove={(model) => { setRemoving({ tenant: t, model }); setRemoveConfirm(''); }}
                          />
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
        <Group p="sm" gap="xs" justify="space-between">
          <Group gap="xs">
            <IconPackage size={16} />
            <Text fw={600} size="sm">Distribution packs ({packs.length})</Text>
          </Group>
          <Text size="xs" c="dimmed">
            One image = one consistent set of model versions. Delete unused packs here;
            Images → Distribution also prunes down to the newest few.
          </Text>
        </Group>
        {loading ? (
          <Group p="xl" justify="center"><Loader /></Group>
        ) : (
          <Table striped highlightOnHover>
            <Table.Thead>
              <Table.Tr>
                <Table.Th>Pack</Table.Th>
                <Table.Th>Models</Table.Th>
                <Table.Th />
              </Table.Tr>
            </Table.Thead>
            <Table.Tbody>
              {packs.map((p) => {
                const users = packInUse(p);
                return (
                  <Table.Tr key={p.image}>
                    <Table.Td>
                      <Group gap={8}>
                        <Code>{p.tag}</Code>
                        {p.image === data?.sourceImage && <Badge size="xs" variant="light">newest</Badge>}
                      </Group>
                    </Table.Td>
                    <Table.Td>
                      <Text size="xs" c="dimmed">
                        {p.models.map((m) => `${m.name} ${m.version}`).join(' · ') || '—'}
                      </Text>
                    </Table.Td>
                    <Table.Td>
                      <Tooltip
                        label={users.length > 0 ? `In use by ${users.join(', ')}` : `Remove ${p.tag} from the registry`}
                        withArrow>
                        <span>
                          <ActionIcon
                            variant="subtle"
                            color="red"
                            disabled={users.length > 0}
                            aria-label={`Delete pack ${p.tag}`}
                            onClick={() => { setRemovingPack(p); setDropPackConfirm(''); }}>
                            <IconTrash size={16} />
                          </ActionIcon>
                        </span>
                      </Tooltip>
                    </Table.Td>
                  </Table.Tr>
                );
              })}
              {packs.length === 0 && (
                <Table.Tr>
                  <Table.Td colSpan={3}>
                    <Text ta="center" c="dimmed" py="lg">
                      No distribution image in the registry.
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
                <Table.Th>Versions in packs</Table.Th>
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
                const versions = [...m.versions].sort((a, b) => cmpVer(b.version, a.version));
                return (
                  <Table.Tr key={m.model}>
                    <Table.Td><Text size="sm" fw={500}>{m.model}</Text></Table.Td>
                    <Table.Td>
                      <Group gap={4}>
                        {versions.map((v) => (
                          <Badge
                            key={v.version}
                            variant={v.version === m.latest ? 'light' : 'outline'}
                            size="sm">
                            {v.version}
                          </Badge>
                        ))}
                      </Group>
                    </Table.Td>
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
        onClose={() => { setInstalling(null); setDropPackConfirm(''); }}
        title={`Install models into ${installing?.slug ?? ''}`}
        size="lg">
        <Stack gap="md">
          <Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
            The container stays up. A snapshot is taken first and restored
            automatically if the import or compile fails, so a broken package
            cannot leave half-applied scripts on the tenant. Checking one model
            also checks everything it depends on.
          </Alert>

          <Select
            label="Pack"
            description={packs.length <= 1
              ? 'Only one pack is in the registry. Publish another distribution image to install a different set of versions.'
              : 'One image is one consistent pack. Changing a model version switches the pack — the other models follow.'}
            placeholder="(pick a distribution image)"
            data={imageOptions}
            value={installImage}
            onChange={switchPack}
            searchable
          />

          {selectedPack && (
            <Group justify="space-between" align="flex-start" wrap="nowrap">
              <Text size="xs" c="dimmed">
                {selectedPack.models.map((m) => `${m.name} ${m.version}`).join(' · ')}
              </Text>
              {packInUse(selectedPack).length === 0 ? (
                <Button
                  size="compact-xs"
                  variant="subtle"
                  color="red"
                  leftSection={<IconTrash size={12} />}
                  onClick={() => { setRemovingPack(selectedPack); setDropPackConfirm(''); }}>
                  Delete this pack
                </Button>
              ) : (
                <Text size="xs" c="dimmed">In use by {packInUse(selectedPack).join(', ')}</Text>
              )}
            </Group>
          )}

          <Stack gap={8}>
            <Group justify="space-between">
              <Text size="sm" fw={500}>Models</Text>
              <Button
                size="compact-xs"
                variant="subtle"
                disabled={!selectedPack}
                onClick={() => setInstallModels(expandSelection(
                  (selectedPack?.models ?? []).map((m) => m.name), models))}>
                Select all in this pack
              </Button>
            </Group>
            {installable.map((m) => {
              const checked = installModels.some((s) => s.toLowerCase() === m.model.toLowerCase());
              const requiredBy = checked ? stillRequired(m.model, installModels, models) : [];
              const onTenant = installing?.installed.some((i) => i.name.toLowerCase() === m.model.toLowerCase());
              const row = installing?.installed.find((i) => i.name.toLowerCase() === m.model.toLowerCase());
              const packVer = versionInPack(selectedPack, m.model);
              const inPack = Boolean(packVer);
              const delta = inPack ? cmpVer(packVer, row?.version) : 0;
              const versionOptions = [...m.versions]
                .sort((a, b) => cmpVer(b.version, a.version))
                .map((v) => ({ value: v.version, label: v.version }));
              return (
                <Group key={m.model} gap="sm" wrap="nowrap" align="flex-start">
                  <Checkbox
                    mt={6}
                    checked={checked}
                    disabled={!inPack || requiredBy.length > 0}
                    onChange={(e) => toggleInstallModel(m.model, inputChecked(e))}
                  />
                  <Stack gap={2} style={{ flex: 1, minWidth: 0 }}>
                    <Group gap={8} wrap="wrap">
                      <Text size="sm">{m.model}</Text>
                      {onTenant && delta > 0 && (
                        <Badge size="xs" color="teal">upgrade {row?.version ?? '?'} → {packVer}</Badge>
                      )}
                      {onTenant && delta < 0 && (
                        <Badge size="xs" color="orange">downgrade {row?.version ?? '?'} → {packVer}</Badge>
                      )}
                      {onTenant && delta === 0 && inPack && (
                        <Badge size="xs" color="gray" variant="light">same {packVer}</Badge>
                      )}
                      {!onTenant && inPack && <Badge size="xs" color="gray">not installed</Badge>}
                      {!inPack && <Badge size="xs" color="gray">not in this pack</Badge>}
                      {requiredBy.length > 0 && (
                        <Text size="xs" c="dimmed">locked — {requiredBy.join(', ')} need this</Text>
                      )}
                    </Group>
                    {m.dependsOn.length > 0 && (
                      <Text size="xs" c="dimmed">depends on {m.dependsOn.join(', ')}</Text>
                    )}
                    {(m.extends ?? []).length > 0 && (
                      <Text size="xs" c="dimmed">extends {(m.extends ?? []).join(', ')}</Text>
                    )}
                  </Stack>
                  <div
                    onClick={(e) => e.stopPropagation()}
                    onMouseDown={(e) => e.stopPropagation()}>
                    <Select
                      size="xs"
                      w={120}
                      allowDeselect={false}
                      comboboxProps={{ withinPortal: true }}
                      data={versionOptions}
                      value={packVer ?? null}
                      disabled={versionOptions.length === 0}
                      onChange={(ver) => { if (ver) pickModelVersion(m.model, ver); }}
                    />
                  </div>
                </Group>
              );
            })}
          </Stack>

          <Group justify="flex-end">
            <Button variant="default" onClick={() => { setInstalling(null); setDropPackConfirm(''); }}>
              Cancel
            </Button>
            <Button
              color="grape"
              disabled={installModels.length === 0 || !installImage
                || installModels.some((n) => !versionInPack(selectedPack, n))}
              onClick={() => void install()}>
              Install {installModels.length} model{installModels.length === 1 ? '' : 's'}
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={Boolean(removingPack)}
        onClose={() => { setRemovingPack(null); setDropPackConfirm(''); }}
        title={`Delete pack ${removingPack?.tag ?? ''}`}>
        <Stack gap="sm">
          <Alert color="red" icon={<IconAlertTriangle size={16} />}>
            Removes <Code>{removingPack?.tag}</Code> from the registry. Tenants that
            already installed these models keep them; you just cannot pick this pack
            again until it is republished.
          </Alert>
          <TextInput
            label={`Type "${removingPack?.tag ?? ''}" to confirm`}
            value={dropPackConfirm}
            onChange={(e) => setDropPackConfirm(e.currentTarget.value)}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => { setRemovingPack(null); setDropPackConfirm(''); }}>
              Cancel
            </Button>
            <Button
              color="red"
              loading={droppingPack}
              disabled={!removingPack || dropPackConfirm !== removingPack.tag}
              onClick={() => { if (removingPack) void dropPack(removingPack); }}>
              Delete pack
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={Boolean(removing)}
        onClose={() => { setRemoving(null); setRemoveConfirm(''); }}
        title={`Remove ${removing?.model ?? ''} from ${removing?.tenant.slug ?? ''}`}
        size="lg">
        <Stack gap="md">
          <Alert color="red" icon={<IconAlertTriangle size={16} />}>
            Cascade: metadata, extension fields on other objects, scripts, menus, and
            the physical tables. A snapshot is taken first and restored automatically
            if the delete or the compile afterwards fails. Core and the tenant stand
            model cannot be removed this way.
          </Alert>
          <TextInput
            label={`Type "${removing?.model ?? ''}" to confirm`}
            value={removeConfirm}
            onChange={(e) => setRemoveConfirm(e.currentTarget.value)}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => { setRemoving(null); setRemoveConfirm(''); }}>
              Cancel
            </Button>
            <Button
              color="red"
              disabled={!removing || removeConfirm !== removing.model}
              onClick={() => void uninstall()}>
              Remove {removing?.model}
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
  onRemove,
}: {
  tenant: CatalogueTenant;
  catalogue: CatalogueModel[];
  onInstall: (tenant: CatalogueTenant, preset?: string[]) => void;
  onRemove: (model: string) => void;
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
            <Table.Th>Available</Table.Th>
            <Table.Th>Depends on</Table.Th>
            <Table.Th>Compile</Table.Th>
            <Table.Th />
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rows.map((m) => {
            const cat = catalogue.find((c) => c.model.toLowerCase() === m.name.toLowerCase());
            const deps = cat?.dependsOn ?? [];
            const available = [...(cat?.versions ?? [])].sort((a, b) => cmpVer(b.version, a.version));
            const holders = installedDependents(m.name, tenant, catalogue);
            const stand = (m.metaId ?? '').toLowerCase() === '7e2c1f0a-9b4d-4e6a-8c3f-1d5a7b9e2c40'
              || m.name.toLowerCase() === 'local'
              || m.name.toLowerCase() === 'tenant';
            const blocked = holders.length > 0 || stand;
            const tip = stand
              ? 'The tenant stand model cannot be deleted'
              : holders.length > 0
                ? `First remove ${holders.join(', ')}`
                : `Remove ${m.name} from this tenant`;
            return (
              <Table.Tr key={m.name}>
                <Table.Td>
                  <UnstyledButton onClick={() => onInstall(tenant, [m.name])}>
                    <Text size="sm" fw={500}>{m.name}</Text>
                  </UnstyledButton>
                </Table.Td>
                <Table.Td>{m.version ?? '—'}</Table.Td>
                <Table.Td>
                  <Group gap={4} wrap="wrap">
                    {available.length === 0
                      ? <Text size="sm">{m.latest ?? '—'}</Text>
                      : available.map((v) => (
                          <Badge
                            key={v.version}
                            size="xs"
                            variant={v.version === m.version ? 'filled' : v.version === m.latest ? 'light' : 'outline'}>
                            {v.version}
                          </Badge>
                        ))}
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
                <Table.Td>
                  <Tooltip label={tip}>
                    <ActionIcon
                      variant="subtle"
                      color="red"
                      disabled={blocked}
                      aria-label={`Remove ${m.name}`}
                      onClick={() => onRemove(m.name)}>
                      <IconTrash size={16} />
                    </ActionIcon>
                  </Tooltip>
                </Table.Td>
              </Table.Tr>
            );
          })}
          {rows.length === 0 && (
            <Table.Tr>
              <Table.Td colSpan={6}>
                <Text size="sm" c="dimmed">No business models installed.</Text>
              </Table.Td>
            </Table.Tr>
          )}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}

