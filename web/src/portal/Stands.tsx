import { useCallback, useEffect, useState } from 'react';
import {
  ActionIcon, Alert, Badge, Button, Card, Code, Group, Loader, Modal, Select, Stack,
  Table, Text, TextInput, Title, Tooltip,
} from '@mantine/core';
import { ApiError, portal, type Member, type Stand, type StandUser } from './api';
import { OneTimeSecret } from './Auth';

const statusColour = (status: string) => ({
  Active: 'green', Suspended: 'orange', Failed: 'red', Provisioning: 'blue', Deleting: 'gray',
}[status] ?? 'gray');

/** Free text under the buttons that says WHY one is unavailable. */
function PlanLine({ stand }: { stand: Stand }) {
  const p = stand.plan;
  const bits = [
    p.officeUsers ? `${p.officeUsers} office users` : null,
    p.fieldAgents ? `${p.fieldAgents} field agents` : null,
    p.backupCadence && p.restoreDays ? `${p.backupCadence} backup, ${p.restoreDays}-day restore` : null,
    p.supportResponse,
    p.availability ? `${p.availability} availability` : null,
  ].filter(Boolean);

  if (bits.length === 0) {
    // An unrecognised plan code promises nothing — deliberately, rather than
    // falling back to the cheapest plan's figures. See PlanCatalog.Describe.
    return <Text size="sm" c="dimmed">Plan: {p.name}. Ask us what it includes.</Text>;
  }
  return <Text size="sm" c="dimmed">{p.name} — {bits.join(' · ')}.</Text>;
}

export function StandList({ onOpen }: { onOpen: (stand: Stand) => void }) {
  const [stands, setStands] = useState<Stand[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    portal.stands()
      .then((r) => setStands(r.tenants))
      .catch((e) => setError(e instanceof ApiError ? e.message : 'Could not load your stands.'));
  }, []);

  if (error) return <Alert color="red" variant="light">{error}</Alert>;
  if (!stands) return <Loader />;

  if (stands.length === 0) {
    return (
      <Alert color="blue" variant="light" title="Nothing here yet">
        <Stack gap={4}>
          <Text size="sm">
            No stand is linked to this address yet. A stand becomes yours automatically once it is
            set up with this e-mail as its administrator.
          </Text>
          <Text size="sm">
            If somebody else set yours up, ask them to add you from their own <b>People</b> tab.
          </Text>
        </Stack>
      </Alert>
    );
  }

  return (
    <Stack>
      {stands.map((s) => (
        <Card key={s.id} withBorder padding="md" radius="md">
          <Group justify="space-between" align="flex-start">
            <Stack gap={4}>
              <Group gap="xs">
                <Text fw={600}>{s.displayName}</Text>
                <Badge color={statusColour(s.status)} variant="light">{s.status}</Badge>
                {s.demo && <Badge color="grape" variant="light">Demo</Badge>}
                <Badge variant="outline">{s.role}</Badge>
              </Group>
              <Text size="sm" c="dimmed">{s.url}</Text>
              <PlanLine stand={s} />
            </Stack>
            <Button variant="light" onClick={() => onOpen(s)}>Manage</Button>
          </Group>
        </Card>
      ))}
    </Stack>
  );
}

export function StandDetail({ id, onBack }: { id: string; onBack: () => void }) {
  const [stand, setStand] = useState<Stand | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [secret, setSecret] = useState<{ user: string; password: string } | null>(null);
  const [tab, setTab] = useState<'actions' | 'people' | 'logs'>('actions');

  const load = useCallback(() => {
    portal.stand(id)
      .then(setStand)
      .catch((e) => setError(e instanceof ApiError ? e.message : 'Could not load this stand.'));
  }, [id]);

  useEffect(load, [load]);

  const act = async (name: string, action: () => Promise<unknown>) => {
    setBusy(name);
    setError(null);
    try {
      await action();
      load();
    } catch (e) {
      // The server's sentence, verbatim. It is the thing that tells a customer
      // whether to wait, to pay, or to write to us.
      setError(e instanceof ApiError ? e.message : 'That did not work.');
    } finally {
      setBusy(null);
    }
  };

  if (error && !stand) return <Alert color="red" variant="light">{error}</Alert>;
  if (!stand) return <Loader />;

  return (
    <Stack>
      <Group justify="space-between">
        <Stack gap={2}>
          <Group gap="xs">
            <Title order={4}>{stand.displayName}</Title>
            <Badge color={statusColour(stand.status)} variant="light">{stand.status}</Badge>
          </Group>
          <Text size="sm" c="dimmed">{stand.url}</Text>
        </Stack>
        <Button variant="subtle" onClick={onBack}>All stands</Button>
      </Group>

      <PlanLine stand={stand} />

      {error && <Alert color="red" variant="light" onClose={() => setError(null)} withCloseButton>{error}</Alert>}
      {secret && <OneTimeSecret label={`New password for ${secret.user}`} value={secret.password} />}

      <Group gap="xs">
        <Button size="xs" variant={tab === 'actions' ? 'filled' : 'subtle'} onClick={() => setTab('actions')}>Actions</Button>
        <Button size="xs" variant={tab === 'people' ? 'filled' : 'subtle'} onClick={() => setTab('people')}>People</Button>
        {stand.can.viewLogs && (
          <Button size="xs" variant={tab === 'logs' ? 'filled' : 'subtle'} onClick={() => setTab('logs')}>Log</Button>
        )}
      </Group>

      {tab === 'actions' && (
        <Stack>
          <ResetPasswordCard stand={stand} onSecret={setSecret} onError={setError} />

          <Card withBorder padding="md" radius="md">
            <Stack gap="sm">
              <Text fw={600}>The stand itself</Text>
              <Group>
                <Gated can={stand.can.restartTenant} why="Restarting is not available right now.">
                  <Button
                    variant="light" loading={busy === 'restart'} disabled={!stand.can.restartTenant}
                    onClick={() => void act('restart', () => portal.restart(stand.id))}>
                    Restart
                  </Button>
                </Gated>

                {stand.status === 'Active' ? (
                  <Gated can={stand.can.stopStartTenant} why="Only the owner can stop a stand.">
                    <Button
                      variant="light" color="orange" loading={busy === 'stop'}
                      disabled={!stand.can.stopStartTenant}
                      onClick={() => void act('stop', () => portal.stop(stand.id))}>
                      Stop
                    </Button>
                  </Gated>
                ) : (
                  <Button
                    variant="light" color="green" loading={busy === 'start'}
                    onClick={() => void act('start', () => portal.start(stand.id))}>
                    Start
                  </Button>
                )}
              </Group>
              <Text size="xs" c="dimmed">
                A restart takes a few seconds and signs everybody out of the stand. Stopping keeps
                all the data — nothing is deleted.
              </Text>
            </Stack>
          </Card>
        </Stack>
      )}

      {tab === 'people' && <People stand={stand} />}
      {tab === 'logs' && <Logs id={stand.id} />}
    </Stack>
  );
}

/** A disabled button with a reason attached, rather than a button that 403s. */
function Gated({ can, why, children }: { can: boolean; why: string; children: React.ReactNode }) {
  if (can) return <>{children}</>;
  return <Tooltip label={why} withArrow>{<span>{children}</span>}</Tooltip>;
}

function ResetPasswordCard({
  stand, onSecret, onError,
}: {
  stand: Stand;
  onSecret: (s: { user: string; password: string }) => void;
  onError: (e: string) => void;
}) {
  const [users, setUsers] = useState<StandUser[] | null>(null);
  const [who, setWho] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);

  useEffect(() => {
    portal.users(stand.id).then((r) => setUsers(r.users)).catch(() => setUsers([]));
  }, [stand.id]);

  const reset = async () => {
    setBusy(true);
    try {
      const result = await portal.resetUserPassword(stand.id, who ?? undefined);
      onSecret({ user: result.user, password: result.password });
      setConfirming(false);
    } catch (e) {
      onError(e instanceof ApiError ? e.message : 'Could not reset that password.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card withBorder padding="md" radius="md">
      <Stack gap="sm">
        <Text fw={600}>Somebody cannot sign in</Text>
        <Group align="flex-end">
          <Select
            label="Which user" placeholder={users ? 'Pick a person' : 'Loading…'}
            style={{ flex: 1 }} searchable value={who} onChange={setWho}
            disabled={!stand.can.resetUserPassword}
            data={(users ?? []).map((u) => ({
              value: u.name,
              label: u.locked ? `${u.name} (locked)` : u.name,
            }))} />
          <Gated can={stand.can.resetUserPassword} why="This stand is read-only at the moment.">
            <Button
              disabled={!stand.can.resetUserPassword || !who}
              onClick={() => setConfirming(true)}>
              Set a new password
            </Button>
          </Gated>
        </Group>
        <Text size="xs" c="dimmed">
          We show the new password once, here. They will be asked to change it when they sign in,
          and a locked account is unlocked by this.
        </Text>
      </Stack>

      <Modal opened={confirming} onClose={() => setConfirming(false)} title="Set a new password?">
        <Stack>
          <Text size="sm">
            This replaces the password for <Code>{who}</Code> on <Code>{stand.slug}</Code>. Their
            current password stops working immediately.
          </Text>
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setConfirming(false)}>Cancel</Button>
            <Button color="orange" loading={busy} onClick={() => void reset()}>Set it</Button>
          </Group>
        </Stack>
      </Modal>
    </Card>
  );
}

function People({ stand }: { stand: Stand }) {
  const [members, setMembers] = useState<Member[] | null>(null);
  const [email, setEmail] = useState('');
  const [role, setRole] = useState<string | null>('Member');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    portal.members(stand.id).then((r) => setMembers(r.members)).catch(() => setMembers([]));
  }, [stand.id]);

  useEffect(load, [load]);

  const add = async () => {
    setBusy(true);
    setError(null);
    try {
      await portal.addMember(stand.id, email, role ?? 'Member');
      setEmail('');
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Could not add them.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Card withBorder padding="md" radius="md">
      <Stack gap="sm">
        <Text fw={600}>Who can manage this stand</Text>
        <Text size="xs" c="dimmed">
          This is about managing the stand from here — not about who can sign in to it. Users
          inside the stand are managed in the stand itself.
        </Text>

        {error && <Alert color="red" variant="light">{error}</Alert>}

        <Table>
          <Table.Tbody>
            {(members ?? []).map((m) => (
              <Table.Tr key={m.id}>
                <Table.Td>{m.displayName ?? m.email}{m.isSelf && <Text span c="dimmed"> — you</Text>}</Table.Td>
                <Table.Td><Badge variant="light">{m.role}</Badge></Table.Td>
                <Table.Td align="right">
                  {stand.can.manageMembers && !m.isSelf && (
                    <ActionIcon
                      variant="subtle" color="red" aria-label="Remove"
                      onClick={() => void portal.removeMember(stand.id, m.id).then(load).catch((e) =>
                        setError(e instanceof ApiError ? e.message : 'Could not remove them.'))}>
                      ×
                    </ActionIcon>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>

        {stand.can.manageMembers && (
          <Group align="flex-end">
            <TextInput
              label="Add somebody" placeholder="their@email" style={{ flex: 1 }}
              value={email} onChange={(e) => setEmail(e.currentTarget.value)} />
            <Select
              label="As" w={130} value={role} onChange={setRole}
              data={['Member', 'Owner']} />
            <Button loading={busy} disabled={!email} onClick={() => void add()}>Add</Button>
          </Group>
        )}
        {stand.can.manageMembers && (
          <Text size="xs" c="dimmed">
            They need their own confirmed account first — ask them to sign up, then add them.
          </Text>
        )}
      </Stack>
    </Card>
  );
}

function Logs({ id }: { id: string }) {
  const [text, setText] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    portal.logs(id, 300)
      .then((r) => setText(r.logs))
      .catch((e) => setError(e instanceof ApiError ? e.message : 'Could not read the log.'));
  }, [id]);

  if (error) return <Alert color="red" variant="light">{error}</Alert>;
  if (text === null) return <Loader />;

  return (
    <Card withBorder padding="md" radius="md">
      <Code block style={{ maxHeight: 460, overflow: 'auto', fontSize: 12 }}>
        {text || 'Nothing in the log yet.'}
      </Code>
    </Card>
  );
}
