import { useCallback, useMemo, useState } from 'react';
import {
  Alert, Badge, Button, Card, Group, Loader, Modal, NumberInput, Select, Stack, Table, Text, TextInput, Title,
} from '@mantine/core';
import { IconAlertTriangle, IconPlus, IconReceipt, IconRefresh } from '@tabler/icons-react';
import { api, type BillingInvoice, type Tenant } from './api';
import { fmt, usePoll } from './shared';

function isoDate(d: Date): string {
  return d.toISOString().slice(0, 10);
}

function addMonths(d: Date, months: number): Date {
  const next = new Date(d);
  next.setUTCMonth(next.getUTCMonth() + months);
  return next;
}

export function BillingPage() {
  const [rows, setRows] = useState<BillingInvoice[]>([]);
  const [tenants, setTenants] = useState<Tenant[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [booksDown, setBooksDown] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [filter, setFilter] = useState<'overdue' | 'all'>('overdue');
  const [standFilter, setStandFilter] = useState<string | null>(null);
  const [issueOpen, setIssueOpen] = useState(false);
  const [issueTenantId, setIssueTenantId] = useState<string | null>(null);
  const [amount, setAmount] = useState<number | string>(100);
  const [currency, setCurrency] = useState('USD');
  const [periodFrom, setPeriodFrom] = useState(isoDate(new Date()));
  const [periodTo, setPeriodTo] = useState(isoDate(addMonths(new Date(), 1)));
  const [dueDate, setDueDate] = useState(isoDate(addMonths(new Date(), 1)));

  const billable = useMemo(
    () => tenants.filter((t) => !t.demo),
    [tenants],
  );

  const refresh = useCallback(async () => {
    try {
      const fleet = await api.listTenants();
      setTenants(fleet);
      let invoices: BillingInvoice[];
      if (filter === 'overdue') {
        invoices = await api.billingInvoices({ overdue: true });
      } else if (standFilter) {
        invoices = await api.billingInvoices({ standSlug: standFilter });
      } else {
        invoices = [];
      }
      setRows(invoices);
      setError(null);
      setBooksDown(false);
    } catch (e) {
      const msg = (e as Error).message;
      if (msg.includes('commercial tenant is down')
        || msg.includes('Billing:TenantSlug')) {
        setBooksDown(true);
        setRows([]);
        setError(null);
      } else {
        setBooksDown(false);
        setError(msg);
      }
    } finally {
      setLoaded(true);
    }
  }, [filter, standFilter]);

  usePoll(refresh, 12_000);

  const action = async (name: string, fn: () => Promise<unknown>) => {
    setBusy(name); setError(null);
    try { await fn(); await refresh(); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(null); }
  };

  const issue = async () => {
    if (!issueTenantId) return;
    const n = typeof amount === 'number' ? amount : Number(amount);
    if (!Number.isFinite(n) || n <= 0) {
      setError('Amount must be a positive number.');
      return;
    }
    await action('issue', () => api.issueInvoice({
      tenantId: issueTenantId,
      amount: n,
      currency: currency.trim() || 'USD',
      periodFrom,
      periodTo,
      dueDate,
    }));
    setIssueOpen(false);
  };

  if (!loaded && !error && !booksDown) {
    return <Group justify="center" p="xl"><Loader /></Group>;
  }

  return (
    <Stack gap="md">
      <Group justify="space-between">
        <div>
          <Title order={3}>Billing</Title>
          <Text size="sm" c="dimmed">Hosting invoices over the commercial tenant</Text>
        </div>
        <Group gap="xs">
          <Button variant="default" leftSection={<IconRefresh size={14} />}
            loading={busy === 'refresh'} onClick={() => void action('refresh', async () => {})}>
            Refresh
          </Button>
          <Button leftSection={<IconPlus size={14} />} onClick={() => {
            setIssueTenantId(billable[0]?.id ?? null);
            setIssueOpen(true);
          }}>Issue</Button>
        </Group>
      </Group>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />}>{error}</Alert>}
      {booksDown && (
        <Alert color="yellow" icon={<IconAlertTriangle size={16} />}>
          The commercial books are unavailable. Set Billing:TenantSlug to an Active stand, or start that stand.
        </Alert>
      )}

      <Group gap="xs">
        <Select
          data={[
            { value: 'overdue', label: 'Overdue' },
            { value: 'all', label: 'By stand' },
          ]}
          value={filter}
          onChange={(v) => setFilter((v as 'overdue' | 'all') ?? 'overdue')}
          w={160}
        />
        {filter === 'all' && (
          <Select
            placeholder="Stand"
            searchable
            clearable
            data={billable.map((t) => ({ value: t.slug, label: t.slug }))}
            value={standFilter}
            onChange={setStandFilter}
            w={220}
          />
        )}
        <Badge size="lg" variant="light" leftSection={<IconReceipt size={14} />}>
          {rows.length} invoice{rows.length === 1 ? '' : 's'}
        </Badge>
      </Group>

      <Card withBorder padding={0}>
        <Table striped highlightOnHover>
          <Table.Thead><Table.Tr>
            <Table.Th>Stand</Table.Th>
            <Table.Th>Number</Table.Th>
            <Table.Th>Due</Table.Th>
            <Table.Th>Remaining</Table.Th>
            <Table.Th>Status</Table.Th>
            <Table.Th />
          </Table.Tr></Table.Thead>
          <Table.Tbody>
            {rows.map((r) => (
              <Table.Tr key={`${r.standSlug}-${r.id}-${r.number}`}>
                <Table.Td><Text fw={600}>{r.standSlug ?? '—'}</Text></Table.Td>
                <Table.Td>{r.number ?? r.id.slice(0, 8)}</Table.Td>
                <Table.Td>{r.dueDate ? fmt(r.dueDate) : '—'}</Table.Td>
                <Table.Td>{r.remaining}</Table.Td>
                <Table.Td>
                  <Badge
                    color={r.status === 'paid' ? 'green' : r.remaining > 0 ? 'orange' : 'gray'}
                    variant="light"
                  >
                    {r.status}
                  </Badge>
                </Table.Td>
                <Table.Td>
                  {r.status !== 'paid' && r.remaining > 0 && r.standSlug && (
                    <Button
                      size="xs"
                      variant="light"
                      loading={busy === `pay:${r.id}`}
                      onClick={() => void action(`pay:${r.id}`, () =>
                        api.bankPayInvoice(r.id || r.number || 'pay', r.standSlug!))}
                    >
                      Bank paid
                    </Button>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
            {rows.length === 0 && !booksDown && (
              <Table.Tr>
                <Table.Td colSpan={6}>
                  <Text ta="center" c="dimmed" py="lg">
                    {filter === 'all' && !standFilter
                      ? 'Pick a stand to list invoices.'
                      : 'No invoices in this view.'}
                  </Text>
                </Table.Td>
              </Table.Tr>
            )}
          </Table.Tbody>
        </Table>
      </Card>

      <Modal opened={issueOpen} onClose={() => setIssueOpen(false)} title="Issue invoice" centered>
        <Stack gap="sm">
          <Select
            label="Stand"
            searchable
            data={billable.map((t) => ({ value: t.id, label: t.slug }))}
            value={issueTenantId}
            onChange={setIssueTenantId}
          />
          <Group grow>
            <NumberInput label="Amount" value={amount} onChange={setAmount} min={0.01} decimalScale={2} />
            <TextInput label="Currency" value={currency} onChange={(e) => setCurrency(e.currentTarget.value)} />
          </Group>
          <Group grow>
            <TextInput label="Period from" type="date" value={periodFrom}
              onChange={(e) => setPeriodFrom(e.currentTarget.value)} />
            <TextInput label="Period to" type="date" value={periodTo}
              onChange={(e) => setPeriodTo(e.currentTarget.value)} />
          </Group>
          <TextInput label="Due" type="date" value={dueDate}
            onChange={(e) => setDueDate(e.currentTarget.value)} />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setIssueOpen(false)}>Cancel</Button>
            <Button loading={busy === 'issue'} disabled={!issueTenantId} onClick={() => void issue()}>
              Issue
            </Button>
          </Group>
        </Stack>
      </Modal>
    </Stack>
  );
}
