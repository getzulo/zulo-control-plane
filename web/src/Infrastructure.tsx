import { useMemo, useState } from 'react';
import {
  Alert, Badge, Button, Card, Code, Grid, Group, Loader, Modal, Stack, Table, Text, TextInput, Title,
} from '@mantine/core';
import { IconAlertTriangle, IconArrowsExchange, IconDatabase } from '@tabler/icons-react';
import { api, type Cluster, type InfraNode } from './api';
import { CHECK_COLOR, ROLE_COLOR, ROLE_LABEL, ROLE_ORDER, ago, usePoll } from './shared';

/** Byte lag reads better as a size than as a number with nine digits. */
const lag = (n?: number | null) =>
  n == null ? '—' : n === 0 ? '0' : n < 1024 ? `${n} B` : n < 1024 * 1024 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`;

function groupNodes(nodes: InfraNode[]) {
  const groups = new Map<string, InfraNode[]>();
  for (const n of nodes) {
    const role = ROLE_ORDER.includes(n.role) ? n.role : 'host';
    const list = groups.get(role) ?? [];
    list.push(n);
    groups.set(role, list);
  }
  return ROLE_ORDER.filter((r) => groups.has(r)).map((role) => ({ role, nodes: groups.get(role)! }));
}

export function InfrastructurePage() {
  const [cluster, setCluster] = useState<Cluster | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [report, setReport] = useState<InfraNode | null>(null);
  const [switchTo, setSwitchTo] = useState<InfraNode | null>(null);
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);

  usePoll(async () => {
    try { setCluster(await api.cluster()); setError(null); }
    catch (e) { setError((e as Error).message); }
    finally { setLoading(false); }
  }, 10_000);

  const nodes = cluster?.nodes ?? [];
  const groups = useMemo(() => groupNodes(nodes), [nodes]);
  const leader = nodes.find((m) => (m.patroniRole ?? '').toLowerCase() === 'leader');

  const doSwitchover = async () => {
    if (!switchTo) return;
    setBusy(true);
    try {
      await api.switchover(switchTo.name, confirm);
      setSwitchTo(null);
      setCluster(await api.cluster());
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  };

  return (
    <Stack gap="md">
      <div>
        <Title order={3}>Infrastructure</Title>
        <Text size="sm" c="dimmed">Every machine the fleet needs — not only Postgres</Text>
      </div>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />} withCloseButton onClose={() => setError(null)}>{error}</Alert>}

      {loading ? <Group justify="center" p="xl"><Loader /></Group> : !cluster?.configured ? (
        <Alert color="yellow">Patroni is not configured on this control plane — set <Code>Patroni__Nodes</Code>.</Alert>
      ) : (
        <Stack gap="lg">
          {!cluster.reachable && (
            <Alert color="red" icon={<IconAlertTriangle size={16} />}>
              No Patroni node answered. Postgres state below is from the last reports and the expected list —
              check the panel's route to port 8008 before assuming the database is down.
            </Alert>
          )}

          {groups.map(({ role, nodes: list }) => (
            <div key={role}>
              <Group gap={8} mb="xs">
                <Badge color={ROLE_COLOR[role] ?? 'gray'} variant="filled">{ROLE_LABEL[role] ?? role}</Badge>
                <Text size="xs" c="dimmed">{list.length} host{list.length === 1 ? '' : 's'}</Text>
              </Group>
              <Grid>
                {list.map((m) => {
                  const check = CHECK_COLOR[m.selfCheck] ?? 'gray';
                  const isLeader = (m.patroniRole ?? '').toLowerCase() === 'leader';
                  const replica = role === 'postgres' && !isLeader && (m.state ?? '') === 'streaming';
                  return (
                    <Grid.Col span={{ base: 12, md: 6 }} key={`${role}:${m.name}`}>
                      <Card withBorder padding="md" h="100%">
                        <Group justify="space-between" mb="xs">
                          <Group gap="xs">
                            <IconDatabase size={18} />
                            <Text fw={600}>{m.name}</Text>
                            {m.patroniRole && (
                              <Badge color={isLeader ? 'grape' : 'blue'} variant="filled">{m.patroniRole}</Badge>
                            )}
                          </Group>
                          {m.state && (
                            <Badge color={m.state === 'running' || m.state === 'streaming' ? 'green' : 'red'} variant="light">
                              {m.state}
                            </Badge>
                          )}
                        </Group>

                        <Table withRowBorders={false} verticalSpacing={4} fz="sm">
                          <Table.Tbody>
                            {m.host && (
                              <Table.Tr>
                                <Table.Td c="dimmed">Address</Table.Td>
                                <Table.Td><Code>{m.host}{m.port ? `:${m.port}` : ''}</Code></Table.Td>
                              </Table.Tr>
                            )}
                            {m.timeline != null && (
                              <Table.Tr><Table.Td c="dimmed">Timeline</Table.Td><Table.Td>{m.timeline}</Table.Td></Table.Tr>
                            )}
                            {role === 'postgres' && !isLeader && m.lag != null && (
                              <Table.Tr>
                                <Table.Td c="dimmed">Replication lag</Table.Td>
                                <Table.Td>
                                  <Badge variant="light" color={m.lag > 16 * 1024 * 1024 ? 'red' : 'green'}>{lag(m.lag)}</Badge>
                                </Table.Td>
                              </Table.Tr>
                            )}
                            <Table.Tr>
                              <Table.Td c="dimmed">Self-check</Table.Td>
                              <Table.Td>
                                <Group gap={6}>
                                  <Badge color={check} variant="light">{m.selfCheck}</Badge>
                                  {m.selfCheckAgeSeconds != null && (
                                    <Text size="xs" c="dimmed">{ago(m.selfCheckAgeSeconds)}</Text>
                                  )}
                                </Group>
                              </Table.Td>
                            </Table.Tr>
                          </Table.Tbody>
                        </Table>

                        {(m.selfCheck === 'stale' || m.selfCheck === 'never') && (
                          <Alert color="orange" mt="xs" p="xs">
                            <Text size="xs">
                              {m.selfCheck === 'never'
                                ? 'This name has never reported. Install the check with CP_URL set, or wait for the panel heartbeat if this is the panel itself.'
                                : `Last report ${ago(m.selfCheckAgeSeconds)} — the check publishes every 5 minutes, so it or the machine has stopped.`}
                            </Text>
                          </Alert>
                        )}

                        <Group mt="sm" gap="xs">
                          <Button size="xs" variant="default" disabled={!m.selfCheckReport} onClick={() => setReport(m)}>
                            Self-check report
                          </Button>
                          {replica && (
                            <Button
                              size="xs" color="grape" variant="light" leftSection={<IconArrowsExchange size={14} />}
                              onClick={() => { setSwitchTo(m); setConfirm(''); }}
                            >
                              Make leader
                            </Button>
                          )}
                        </Group>
                      </Card>
                    </Grid.Col>
                  );
                })}
              </Grid>
            </div>
          ))}

          {nodes.length === 0 && (
            <Alert color="yellow">Nothing to show — Patroni did not answer and no host has reported.</Alert>
          )}
        </Stack>
      )}

      <Modal opened={Boolean(report)} onClose={() => setReport(null)} size="xl" title={`Self-check — ${report?.name ?? ''}`}>
        <Code block style={{ fontSize: 12, whiteSpace: 'pre-wrap' }}>{report?.selfCheckReport}</Code>
      </Modal>

      <Modal opened={Boolean(switchTo)} onClose={() => !busy && setSwitchTo(null)} title="Hand over the leader role">
        <Stack gap="sm">
          <Alert color="orange" icon={<IconAlertTriangle size={16} />}>
            Every open connection to <b>{leader?.name}</b> is dropped. Every tenant sees a brief error while
            connections re-establish against <b>{switchTo?.name}</b>.
          </Alert>
          <TextInput
            label={`Type "${switchTo?.name}" to confirm`} value={confirm} disabled={busy}
            onChange={(e) => setConfirm(e.currentTarget.value)}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setSwitchTo(null)} disabled={busy}>Cancel</Button>
            <Button color="grape" loading={busy} disabled={confirm !== switchTo?.name} onClick={() => void doSwitchover()}>
              Switch over
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
