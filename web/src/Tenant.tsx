import { useCallback, useState } from 'react';
import { useParams, Link } from 'react-router-dom';
import {
  Alert, Anchor, Badge, Button, Card, Code, Grid, Group, Loader, Modal, Progress, Select, SimpleGrid, Stack, Table, Tabs,
  Text, TextInput, Title, Tooltip,
} from '@mantine/core';
import {
  IconAlertTriangle, IconArrowLeft, IconArrowUp, IconCamera, IconKey, IconPlayerPlay, IconPlayerStop, IconRotate, IconTrash, IconUnlink,
} from '@tabler/icons-react';
import { api, type ImageTag, type Job, type Snapshot, type Tenant, type TenantModels, type TenantStats } from './api';
import { JOB_COLOR, JobProgress, STATUS_COLOR, fmt, fmtBytes, useJob, usePoll } from './shared';

/**
 * One number with its context. A bar only when there is a ceiling to be a
 * fraction OF — CPU and a memory limit have one, a database size does not, and a
 * bar with an invented maximum is worse than no bar.
 */
function Meter({ label, value, bar, hint }: { label: string; value: string; bar?: number; hint?: string }) {
  const pct = bar == null || !Number.isFinite(bar) ? null : Math.min(100, Math.max(0, bar));
  return (
    <Card withBorder padding="sm">
      <Text size="xs" c="dimmed" tt="uppercase" fw={600}>{label}</Text>
      <Text fz={22} fw={700} lh={1.2}>{value}</Text>
      {pct != null && (
        <Progress value={pct} size="xs" mt={4} color={pct > 90 ? 'red' : pct > 70 ? 'yellow' : 'blue'} />
      )}
      {hint && <Text size="xs" c="dimmed" mt={2} lineClamp={1}>{hint}</Text>}
    </Card>
  );
}

export function TenantPage() {
  const { id = '' } = useParams();
  const [tenant, setTenant] = useState<Tenant | null>(null);
  const [jobs, setJobs] = useState<Job[]>([]);
  const [snapshots, setSnapshots] = useState<Snapshot[]>([]);
  const [logs, setLogs] = useState('');
  const [stats, setStats] = useState<TenantStats | null>(null);
  const [models, setModels] = useState<TenantModels | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [jobId, setJobId] = useState<string | null>(null);
  const [destroy, setDestroy] = useState<'delete' | 'release' | null>(null);
  const [confirm, setConfirm] = useState('');
  const [revealed, setRevealed] = useState<{ user: string; password: string } | null>(null);
  // Upgrade and password reset, both driven from here rather than from another
  // screen — this is the page somebody is on when they need either.
  const [running, setRunning] = useState<Awaited<ReturnType<typeof api.running>> | null>(null);
  const [upgrading, setUpgrading] = useState(false);
  const [releases, setReleases] = useState<ImageTag[]>([]);
  const [targetImage, setTargetImage] = useState<string | null>(null);
  const [resetting, setResetting] = useState(false);
  const [users, setUsers] = useState<{ name: string; email?: string | null; locked: boolean }[]>([]);
  const [resetUser, setResetUser] = useState<string | null>(null);
  const job = useJob(jobId);

  const openUpgrade = async () => {
    setUpgrading(true); setConfirm(''); setTargetImage(null);
    try {
      const i = await api.images();
      // Releases only. A CI build is not something to pin a customer to, and
      // offering both here would make the wrong choice one click away.
      setReleases(i.releases.filter((r) => r.image !== tenant?.imageTag));
    } catch (e) { setError((e as Error).message); }
  };

  const openReset = async () => {
    setResetting(true); setConfirm(''); setResetUser(null);
    try {
      const u = await api.tenantUsers(id);
      setUsers(u);
      setResetUser(u.find((x) => x.name === 'admin')?.name ?? u[0]?.name ?? null);
    } catch (e) { setError((e as Error).message); }
  };

  const refresh = useCallback(async () => {
    try {
      // allSettled: reading the tenant's own database is the one call here
      // that depends on the tenant being reachable, and it must not cost the
      // page when it is not.
      const [t, j, s, m] = await Promise.all([
        api.tenant(id), api.jobs({ tenantId: id, limit: 20 }), api.snapshots(id),
        api.models(id).catch(() => null),
      ]);
      setModels(m);
      setTenant(t); setJobs(j); setSnapshots(s.snapshots); setError(null);
    } catch (e) { setError((e as Error).message); }
    finally { setLoading(false); }
  }, [id]);

  usePoll(refresh, 10_000);

  // Separate and slower. Reading stats costs about a second — a real CPU
  // percentage needs two samples from the Docker stats stream — so it must not sit
  // in the path that keeps the rest of the page current.
  usePoll(useCallback(async () => {
    try { setStats(await api.stats(id)); } catch { /* shown as unavailable below */ }
    try { setRunning(await api.running(id)); } catch { /* likewise */ }
  }, [id]), 20_000);

  const act = async (fn: () => Promise<unknown>) => {
    try { await fn(); void refresh(); } catch (e) { setError((e as Error).message); }
  };

  const loadLogs = async () => {
    setLogs('Loading…');
    try { setLogs((await api.logs(id, 400)).logs || '(empty)'); }
    catch (e) { setLogs(`Could not read logs: ${(e as Error).message}`); }
  };

  if (loading) return <Group justify="center" p="xl"><Loader /></Group>;
  if (!tenant) return <Alert color="red">{error ?? 'Tenant not found'}</Alert>;

  const isCopy = Boolean(tenant.restoredFromSlug);
  // The panel did not create this one's database, so it must not offer to
  // destroy it — the API refuses either way.
  const adopted = tenant.origin === 'Adopted';

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <Group gap="sm">
          <Anchor component={Link} to="/tenants"><IconArrowLeft size={18} /></Anchor>
          <div>
            <Group gap={8}>
              <Title order={3}>{tenant.slug}</Title>
              <Badge color={STATUS_COLOR[tenant.status] ?? 'gray'} variant="light">{tenant.status}</Badge>
              <Badge color={tenant.health === 'Ok' ? 'green' : tenant.health === 'Down' ? 'red' : 'gray'} variant="dot">
                {tenant.health}
              </Badge>
              {isCopy && <Badge color="grape" variant="filled">restored copy</Badge>}
            </Group>
            <Anchor size="sm" href={`https://${tenant.slug}.zulo.one`} target="_blank" rel="noreferrer">
              {tenant.slug}.zulo.one
            </Anchor>
          </div>
        </Group>
        <Group gap="xs">
          {tenant.status === 'Suspended'
            ? <Button size="xs" variant="default" leftSection={<IconPlayerPlay size={14} />} onClick={() => void act(() => api.start(id))}>Start</Button>
            : <Button size="xs" variant="default" leftSection={<IconPlayerStop size={14} />} onClick={() => void act(() => api.stop(id))}>Stop</Button>}
          <Button size="xs" variant="default" leftSection={<IconRotate size={14} />} onClick={() => void act(() => api.restart(id))}>Restart</Button>
          <Button
            size="xs" variant="light" leftSection={<IconCamera size={14} />}
            disabled={!tenant.databaseName}
            onClick={async () => { try { setJobId((await api.takeSnapshot(id)).jobId); } catch (e) { setError((e as Error).message); } }}
          >
            Snapshot
          </Button>
          {/* The two things an operator comes to a tenant's page to do, and which
              used to live nowhere or on another screen entirely. */}
          <Button
            size="xs" variant="light" color="orange" leftSection={<IconArrowUp size={14} />}
            onClick={() => { void openUpgrade(); }}
          >
            Upgrade
          </Button>
          <Button
            size="xs" variant="light" color="yellow" leftSection={<IconKey size={14} />}
            disabled={!tenant.databaseName}
            onClick={() => { void openReset(); }}
          >
            Reset password
          </Button>
          <Button size="xs" color={adopted ? 'blue' : 'red'} variant="light"
            leftSection={adopted ? <IconUnlink size={14} /> : <IconTrash size={14} />}
            onClick={() => { setDestroy(adopted ? 'release' : 'delete'); setConfirm(''); }}>
            {adopted ? 'Stop managing' : 'Delete'}
          </Button>
        </Group>
      </Group>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />} withCloseButton onClose={() => setError(null)}>{error}</Alert>}
      {tenant.lastError && <Alert color="red" title="Last failure">{tenant.lastError}</Alert>}

      {isCopy && (
        <Alert color="grape">
          A restore of <b>{tenant.restoredFromSlug}</b> taken {fmt(tenant.restoredAt)}. It is disposable: swap it in
          from the Backups screen, or delete it. It costs a container and a database until you do.
        </Alert>
      )}

      {tenant.previousDatabaseName && (
        <Alert color="orange">
          A swap set <Code>{tenant.previousDatabaseName}</Code> aside on {fmt(tenant.previousDatabaseAt)}. That is the
          only way back to the data from before it — discard it from the Backups screen when you are satisfied.
        </Alert>
      )}

      {job && <Card withBorder padding="sm"><JobProgress job={job} /></Card>}

      {/* What it is COSTING, right now, on both sides. The panel had plenty about
          how a tenant was configured and nothing about what it was doing. */}
      <SimpleGrid cols={{ base: 2, sm: 4 }}>
        <Meter
          label="CPU"
          value={stats?.container ? `${stats.container.cpuPercent.toFixed(1)}%` : '—'}
          bar={stats?.container?.cpuPercent}
          hint={stats?.container ? `container ${stats.container.state}` : 'no container'}
        />
        <Meter
          label="Memory"
          value={stats?.container ? fmtBytes(stats.container.memoryBytes) : '—'}
          bar={stats?.container?.memoryPercent}
          // A limit of 0 means unlimited, which must not render as "0% of 0 B".
          hint={stats?.container?.memoryLimitBytes
            ? `of ${fmtBytes(stats.container.memoryLimitBytes)}`
            : 'no limit set'}
        />
        <Meter
          label="Database"
          value={stats?.database ? fmtBytes(stats.database.sizeBytes) : '—'}
          hint={stats?.database
            ? `${stats.database.tableCount} tables · largest ${stats.database.largestTableName ?? '—'}`
            : 'not readable'}
        />
        <Meter
          label="Connections"
          value={stats?.database ? String(stats.database.connections) : '—'}
          bar={stats?.database ? (stats.database.connections * 100) / stats.database.maxConnections : undefined}
          hint={stats?.database ? `of ${stats.database.maxConnections} cluster-wide` : ''}
        />
      </SimpleGrid>

      {stats?.error && (
        // Partial answers are useful: which HALF failed is the actionable part.
        <Alert color="yellow" p="xs"><Text size="xs">Some statistics could not be read — {stats.error}</Text></Alert>
      )}

      {stats?.container && (
        <Text size="xs" c="dimmed">
          Up since {fmt(stats.container.startedAt)} · {stats.container.restartCount} restarts ·
          {' '}net {fmtBytes(stats.container.networkRxBytes)} in / {fmtBytes(stats.container.networkTxBytes)} out
        </Text>
      )}

      <Grid>
        <Grid.Col span={{ base: 12, md: 6 }}>
          <Card withBorder padding="md" h="100%">
            <Text fw={600} mb="xs">Deployment</Text>
            <Table withRowBorders={false} verticalSpacing={5} fz="sm">
              <Table.Tbody>
                <Table.Tr><Table.Td c="dimmed">Image</Table.Td><Table.Td><Code>{tenant.imageTag}</Code></Table.Td></Table.Tr>
                <Table.Tr>
                  <Table.Td c="dimmed">Running</Table.Td>
                  <Table.Td>
                    {/* What the container REPORTS, beside the tag pinned in the
                        registry. They disagree when a container was replaced
                        outside the panel, and that gap is worth seeing. */}
                    {!running ? '—' : !running.reachable ? (
                      <Badge color="red" variant="light">not answering</Badge>
                    ) : (
                      <Group gap={6}>
                        <Code>{running.version}{running.build ? ` · ${running.build}` : ''}</Code>
                        {running.matchesPinned === false && (
                          <Badge color="orange" variant="light">differs from the pinned tag</Badge>
                        )}
                      </Group>
                    )}
                  </Table.Td>
                </Table.Tr>
                <Table.Tr><Table.Td c="dimmed">Container</Table.Td><Table.Td><Code>{tenant.containerId ?? '—'}</Code></Table.Td></Table.Tr>
                <Table.Tr><Table.Td c="dimmed">Database</Table.Td><Table.Td><Code>{tenant.databaseName ?? '—'}</Code></Table.Td></Table.Tr>
                <Table.Tr><Table.Td c="dimmed">Administrator</Table.Td><Table.Td>{tenant.adminEmail ?? '—'}</Table.Td></Table.Tr>
                <Table.Tr><Table.Td c="dimmed">Created</Table.Td><Table.Td>{fmt(tenant.createdAt)}</Table.Td></Table.Tr>
                <Table.Tr><Table.Td c="dimmed">Last probe</Table.Td><Table.Td>{fmt(tenant.lastHealthAt)}</Table.Td></Table.Tr>
              </Table.Tbody>
            </Table>
            {tenant.hasUnreadAdminPassword && (
              <Alert color="blue" mt="sm" p="xs">
                <Text size="xs" mb={4}>
                  The invitation could not be sent, so the one-time password is held here. Reading it erases it.
                </Text>
                <Button size="xs" onClick={async () => {
                  try { const r = await api.adminPassword(id); setRevealed(r); void refresh(); }
                  catch (e) { setError((e as Error).message); }
                }}>Show it once</Button>
              </Alert>
            )}
          </Card>
        </Grid.Col>

        <Grid.Col span={12}>
          <Card withBorder padding="md">
            <Group justify="space-between" mb="xs">
              <Text fw={600}>Business layer</Text>
              {models && (
                <Group gap={6}>
                  {models.platformBuild && (
                    <Tooltip label="The platform build this image was cut from" withArrow>
                      <Badge variant="light" color="gray">platform {models.platformBuild}</Badge>
                    </Tooltip>
                  )}
                  {!models.carriesPackages && (
                    <Tooltip label="A platform-only image ships no models — that is why there is nothing to install." withArrow>
                      <Badge color="orange">image carries no packages</Badge>
                    </Tooltip>
                  )}
                </Group>
              )}
            </Group>

            {!models ? <Text size="sm" c="dimmed">Reading…</Text>
             : models.error ? <Alert color="yellow">{models.error}</Alert>
             : models.models.length === 0 ? (
              // The state every tenant was in before packages existed, and the one
              // thing the fleet list could never show.
              <Text size="sm" c="dimmed">
                Nothing installed — not even Core. Move this tenant onto a distribution image
                (<Code>zuloone:…</Code> rather than <Code>zuloone-core:…</Code>) and it installs its models on
                the next boot.
              </Text>
             ) : (
              <Table fz="sm">
                <Table.Thead>
                  <Table.Tr>
                    <Table.Th>Model</Table.Th><Table.Th>Installed</Table.Th>
                    <Table.Th>Image offers</Table.Th><Table.Th>Compiles</Table.Th>
                  </Table.Tr>
                </Table.Thead>
                <Table.Tbody>
                  {models.models.map((m) => (
                    <Table.Tr key={m.name}>
                      <Table.Td>
                        <Group gap={6}>
                          <Text size="sm" fw={m.isSystem ? 600 : 400}>{m.name}</Text>
                          {m.isSystem && (
                            <Tooltip label="The platform's own model. Its version IS the application version." withArrow>
                              <Badge size="xs" color="gray">system</Badge>
                            </Tooltip>
                          )}
                          {!m.isEnabled && (
                            <Tooltip label="Disabled models are invisible at run time — no menus, no commands, no scripts." withArrow>
                              <Badge size="xs" color="red">disabled</Badge>
                            </Tooltip>
                          )}
                        </Group>
                      </Table.Td>
                      <Table.Td><Code fz={11}>{m.version ?? '—'}</Code></Table.Td>
                      <Table.Td>
                        {m.offers
                          ? <Group gap={4}>
                              <Code fz={11}>{m.offers}</Code>
                              {m.behind && <Badge size="xs" color="orange">newer</Badge>}
                            </Group>
                          : <Text size="xs" c="dimmed">—</Text>}
                      </Table.Td>
                      <Table.Td>
                        {m.compilationStatus
                          ? <Tooltip label={m.compilationError ?? 'No error recorded'} multiline w={360} withArrow disabled={!m.compilationError}>
                              <Badge color={m.compilationStatus === 'Ok' ? 'green' : 'red'}>{m.compilationStatus}</Badge>
                            </Tooltip>
                          : <Text size="xs" c="dimmed">—</Text>}
                      </Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
             )}

            {models && models.notInstalled.length > 0 && (
              // Carried by the image and absent from the database: either the
              // tenant's allow-list excludes it, or the install failed. Both are
              // worth seeing; neither is visible anywhere else.
              <Alert color="gray" variant="light" mt="sm">
                <Text size="xs">
                  In the image but not installed: {models.notInstalled.map((n) => `${n.name} ${n.version}`).join(', ')}.
                  Either this tenant's model list excludes them, or the install did not run.
                </Text>
              </Alert>
            )}
          </Card>
        </Grid.Col>

        <Grid.Col span={{ base: 12, md: 6 }}>
          <Card withBorder padding="md" h="100%">
            <Text fw={600} mb="xs">Snapshots ({snapshots.length})</Text>
            {snapshots.length === 0 ? (
              <Text size="sm" c="dimmed">None yet. A snapshot is what makes an upgrade reversible.</Text>
            ) : (
              <Stack gap={6}>
                {snapshots.slice(0, 6).map((s) => (
                  <Group key={s.id} justify="space-between">
                    <div>
                      <Text size="sm">{fmt(s.createdAt)}</Text>
                      <Text size="xs" c="dimmed">{s.kind}{s.note ? ` · ${s.note}` : ''}</Text>
                    </div>
                    <Badge variant="light" color={s.onDisk ? 'gray' : 'red'}>
                      {s.onDisk ? fmtBytes(s.sizeBytes) : 'file missing'}
                    </Badge>
                  </Group>
                ))}
              </Stack>
            )}
          </Card>
        </Grid.Col>
      </Grid>

      <Card withBorder padding={0}>
        <Tabs defaultValue="activity" onChange={(v) => { if (v === 'logs' && !logs) void loadLogs(); }}>
          <Tabs.List>
            <Tabs.Tab value="activity">Activity</Tabs.Tab>
            <Tabs.Tab value="logs">Container logs</Tabs.Tab>
          </Tabs.List>

          <Tabs.Panel value="activity" p="sm">
            {jobs.length === 0 ? <Text c="dimmed" size="sm">Nothing has run against this tenant yet.</Text> : (
              <Table fz="sm">
                <Table.Thead><Table.Tr>
                  <Table.Th>What</Table.Th><Table.Th>State</Table.Th><Table.Th>Step</Table.Th>
                  <Table.Th>Started</Table.Th><Table.Th>By</Table.Th>
                </Table.Tr></Table.Thead>
                <Table.Tbody>
                  {jobs.map((j) => (
                    <Table.Tr key={j.id} style={{ cursor: 'pointer' }} onClick={() => setJobId(j.id)}>
                      <Table.Td>{j.kind}</Table.Td>
                      <Table.Td><Badge size="sm" variant="light" color={JOB_COLOR[j.state] ?? 'gray'}>{j.state}</Badge></Table.Td>
                      <Table.Td><Text size="xs" c={j.error ? 'red' : undefined} lineClamp={1}>{j.error ?? j.step}</Text></Table.Td>
                      <Table.Td><Text size="xs">{fmt(j.startedAt ?? j.createdAt)}</Text></Table.Td>
                      <Table.Td><Text size="xs" c="dimmed">{j.createdBy ?? '—'}</Text></Table.Td>
                    </Table.Tr>
                  ))}
                </Table.Tbody>
              </Table>
            )}
          </Tabs.Panel>

          <Tabs.Panel value="logs" p="sm">
            <Group justify="flex-end" mb="xs">
              <Button size="xs" variant="default" onClick={() => void loadLogs()}>Reload</Button>
            </Group>
            <Code block style={{ fontSize: 11, whiteSpace: 'pre-wrap', maxHeight: '60vh', overflow: 'auto' }}>
              {logs || 'Open this tab to load the last 400 lines.'}
            </Code>
          </Tabs.Panel>
        </Tabs>
      </Card>

      <Modal opened={Boolean(revealed)} onClose={() => setRevealed(null)} title="One-time administrator password">        <Stack gap="sm">
          <Text size="sm">
            Already erased from the registry — closing this dialog loses it.
          </Text>
          <Code block>{revealed && `https://${tenant.slug}.zulo.one\n${revealed.user} / ${revealed.password}`}</Code>
          <Group justify="flex-end"><Button onClick={() => setRevealed(null)}>I have copied it</Button></Group>
        </Stack>
      </Modal>

      <Modal opened={destroy !== null} onClose={() => setDestroy(null)} title={destroy === 'release' ? 'Stop managing' : 'Delete tenant'}>        <Stack gap="sm">
          {destroy === 'delete' ? (
            <Alert color="red" icon={<IconAlertTriangle size={16} />}>
              This destroys the container, the database, the role and the key ring. Snapshots survive, and are the
              only thing that would.
            </Alert>
          ) : (
            <Alert color="blue">
              The registry row goes. The container keeps running and the data stays exactly where it is — the panel
              simply stops managing this tenant.
            </Alert>
          )}
          {/* Both are offered here, because the difference only matters at this
              moment and getting it wrong destroys data. An adopted tenant gets no
              such choice: the panel did not create its database and the API
              refuses to delete it, so offering the button would only produce a
              409 after the operator had typed the slug. */}
          {adopted ? (
            <Alert color="gray" variant="light">
              <b>{tenant.slug}</b> was adopted, not created here. Its database and container existed before the panel
              did, so destroying them is not this panel's to offer.
            </Alert>
          ) : (
            <Button variant="subtle" size="xs" onClick={() => setDestroy(destroy === 'delete' ? 'release' : 'delete')}>
              {destroy === 'delete' ? 'I only want to stop managing it →' : '← I really want to destroy it'}
            </Button>
          )}
          <TextInput label={`Type "${tenant.slug}" to confirm`} value={confirm} onChange={(e) => setConfirm(e.currentTarget.value)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setDestroy(null)}>Cancel</Button>
            <Button
              color={destroy === 'delete' ? 'red' : 'blue'} disabled={confirm !== tenant.slug}
              onClick={async () => {
                const mode = destroy; setDestroy(null);
                try {
                  if (mode === 'delete') await api.remove(id, tenant.slug);
                  else await api.release(id, tenant.slug);
                  location.href = '/tenants';
                } catch (e) { setError((e as Error).message); }
              }}
            >
              {destroy === 'delete' ? 'Destroy it' : 'Release it'}
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={upgrading} onClose={() => setUpgrading(false)} title={`Upgrade ${tenant.slug}`}>
        <Stack gap="sm">
          <Alert color="orange" icon={<IconAlertTriangle size={16} />}>
            A snapshot is taken first and it IS the rollback — migrations only run forward, so nothing else can undo
            one. The tenant is unavailable while it boots: migrations, schema sync and a metadata compile run against
            its real data. If the new image will not start, the old tag is pinned back automatically.
          </Alert>
          <Select
            label="Release" placeholder="pick a version" searchable
            description="Releases only — a CI build is not something to pin a customer to."
            data={releases.map((r) => ({ value: r.image, label: r.tag }))}
            value={targetImage} onChange={setTargetImage}
          />
          {releases.length === 0 && (
            <Text size="xs" c="dimmed">
              No other release is published. Releases come from a git tag; see the Images screen.
            </Text>
          )}
          <TextInput label={`Type "${tenant.slug}" to confirm`} value={confirm} onChange={(e) => setConfirm(e.currentTarget.value)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setUpgrading(false)}>Cancel</Button>
            <Button
              color="orange" disabled={!targetImage || confirm !== tenant.slug}
              onClick={async () => {
                const img = targetImage!; setUpgrading(false);
                try { setJobId((await api.upgrade(id, img)).jobId); } catch (e) { setError((e as Error).message); }
              }}
            >
              Snapshot and upgrade
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal opened={resetting} onClose={() => setResetting(false)} title={`Reset a password on ${tenant.slug}`}>
        <Stack gap="sm">
          <Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
            The new password is shown <b>once</b> and stored nowhere. The account is unlocked and asked to change it
            at the next sign-in — and whoever is using it right now will be locked out.
          </Alert>
          <Select
            label="Account" searchable
            data={users.map((u) => ({ value: u.name, label: u.email ? `${u.name} — ${u.email}` : u.name }))}
            value={resetUser} onChange={setResetUser}
          />
          <TextInput label={`Type "${tenant.slug}" to confirm`} value={confirm} onChange={(e) => setConfirm(e.currentTarget.value)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setResetting(false)}>Cancel</Button>
            <Button
              color="yellow" disabled={!resetUser || confirm !== tenant.slug}
              onClick={async () => {
                const who = resetUser!; setResetting(false);
                try {
                  const r = await api.resetPassword(id, tenant.slug, who);
                  setRevealed({ user: r.user, password: r.password });
                } catch (e) { setError((e as Error).message); }
              }}
            >
              Reset it
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
