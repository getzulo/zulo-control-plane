import { useState } from 'react';
import {
  Alert, Badge, Button, Card, Code, Grid, Group, Loader, Modal, Stack, Table, Text, TextInput, Title,
} from '@mantine/core';
import { IconAlertTriangle, IconArrowsExchange, IconDatabase } from '@tabler/icons-react';
import { api, type Cluster, type ClusterMember } from './api';
import { CHECK_COLOR, ago, fmt, usePoll } from './shared';

/** Byte lag reads better as a size than as a number with nine digits. */
const lag = (n?: number | null) =>
  n == null ? '—' : n === 0 ? '0' : n < 1024 ? `${n} B` : n < 1024 * 1024 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`;

export function InfrastructurePage() {
  const [cluster, setCluster] = useState<Cluster | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [report, setReport] = useState<ClusterMember | null>(null);
  const [switchTo, setSwitchTo] = useState<ClusterMember | null>(null);
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);

  usePoll(async () => {
    try { setCluster(await api.cluster()); setError(null); }
    catch (e) { setError((e as Error).message); }
    finally { setLoading(false); }
  }, 10_000);

  const leader = cluster?.members.find((m) => m.role.toLowerCase() === 'leader');

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
        <Text size="sm" c="dimmed">The Postgres cluster every tenant sits on</Text>
      </div>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />} withCloseButton onClose={() => setError(null)}>{error}</Alert>}

      {loading ? <Group justify="center" p="xl"><Loader /></Group> : !cluster?.configured ? (
        <Alert color="yellow">Patroni is not configured on this control plane — set <Code>Patroni__Nodes</Code>.</Alert>
      ) : !cluster.reachable ? (
        // Distinct from "no members": nobody answered at all, which is the more
        // alarming of the two and must not be shown as an empty table.
        <Alert color="red" icon={<IconAlertTriangle size={16} />}>
          No Patroni node answered. The cluster may be fine and unreachable from here — check the panel's route to
          port 8008 before assuming the database is down.
        </Alert>
      ) : (
        <Grid>
          {cluster.members.map((m) => {
            const isLeader = m.role.toLowerCase() === 'leader';
            const check = CHECK_COLOR[m.selfCheck] ?? 'gray';
            return (
              <Grid.Col span={{ base: 12, md: 6 }} key={m.name}>
                <Card withBorder padding="md" h="100%">
                  <Group justify="space-between" mb="xs">
                    <Group gap="xs">
                      <IconDatabase size={18} />
                      <Text fw={600}>{m.name}</Text>
                      <Badge color={isLeader ? 'grape' : 'blue'} variant="filled">{m.role}</Badge>
                    </Group>
                    <Badge color={m.state === 'running' || m.state === 'streaming' ? 'green' : 'red'} variant="light">
                      {m.state}
                    </Badge>
                  </Group>

                  <Table withRowBorders={false} verticalSpacing={4} fz="sm">
                    <Table.Tbody>
                      <Table.Tr><Table.Td c="dimmed">Address</Table.Td><Table.Td><Code>{m.host}:{m.port}</Code></Table.Td></Table.Tr>
                      <Table.Tr><Table.Td c="dimmed">Timeline</Table.Td><Table.Td>{m.timeline ?? '—'}</Table.Td></Table.Tr>
                      {!isLeader && (
                        <Table.Tr>
                          <Table.Td c="dimmed">Replication lag</Table.Td>
                          <Table.Td>
                            <Badge variant="light" color={(m.lag ?? 0) > 16 * 1024 * 1024 ? 'red' : 'green'}>{lag(m.lag)}</Badge>
                          </Table.Td>
                        </Table.Tr>
                      )}
                      <Table.Tr>
                        <Table.Td c="dimmed">Self-check</Table.Td>
                        <Table.Td>
                          <Group gap={6}>
                            <Badge color={check} variant="light">{m.selfCheck}</Badge>
                            <Text size="xs" c="dimmed">{ago(m.selfCheckAgeSeconds)}</Text>
                          </Group>
                        </Table.Td>
                      </Table.Tr>
                    </Table.Tbody>
                  </Table>

                  {/* Silence is the alarm: the node publishes every five minutes
                      whether or not anything is wrong, so a gap means the check or
                      the machine stopped — not that all is well. */}
                  {(m.selfCheck === 'stale' || m.selfCheck === 'never') && (
                    <Alert color="orange" mt="xs" p="xs">
                      <Text size="xs">
                        {m.selfCheck === 'never'
                          ? 'This node has never reported. Install the check with CP_URL set.'
                          : `Last report ${ago(m.selfCheckAgeSeconds)} — the check publishes every 5 minutes, so it or the machine has stopped.`}
                      </Text>
                    </Alert>
                  )}

                  <Group mt="sm" gap="xs">
                    <Button size="xs" variant="default" disabled={!m.selfCheckReport} onClick={() => setReport(m)}>
                      Self-check report
                    </Button>
                    {!isLeader && m.state === 'streaming' && (
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

          {/* etcd witness and anything else that reports but is not a Patroni
              member — otherwise it would simply be invisible. */}
          {cluster.unmatchedReports.map((r) => (
            <Grid.Col span={{ base: 12, md: 6 }} key={r.node}>
              <Card withBorder padding="md" h="100%">
                <Group justify="space-between" mb="xs">
                  <Text fw={600}>{r.node}</Text>
                  <Badge color={CHECK_COLOR[r.status] ?? 'gray'} variant="light">{r.status}</Badge>
                </Group>
                <Text size="sm" c="dimmed">Reports in, but is not a Patroni member.</Text>
                <Text size="xs" c="dimmed" mt={4}>Last heard {fmt(r.receivedAt)}</Text>
              </Card>
            </Grid.Col>
          ))}
        </Grid>
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
