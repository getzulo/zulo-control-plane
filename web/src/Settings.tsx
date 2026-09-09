import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  Alert, Autocomplete, Badge, Button, Card, Code, Group, Loader, NumberInput, Stack, Switch, Text,
  TextInput, Title, Tooltip,
} from '@mantine/core';
import { IconAlertTriangle, IconArrowBackUp, IconDeviceFloppy } from '@tabler/icons-react';
import { api, type SettingDef, type SettingGroup } from './api';

const SOURCE_LABEL: Record<SettingDef['source'], { text: string; color: string; hint: string }> = {
  Database: { text: 'set here', color: 'blue', hint: 'Overridden in this panel. Revert to fall back to cp.env.' },
  Config: { text: 'cp.env', color: 'gray', hint: 'Coming from the deployment configuration.' },
  Default: { text: 'default', color: 'dark', hint: 'Nothing has been said about this key; the built-in default applies.' },
};

/** Bytes are stored as bytes and edited in MB — nobody types 2147483648. */
const MB = 1024 * 1024;

export function SettingsPage() {
  const [groups, setGroups] = useState<SettingGroup[] | null>(null);
  const [images, setImages] = useState<string[]>([]);
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const [s, i] = await Promise.allSettled([api.settings(), api.images()]);
      if (s.status === 'fulfilled') { setGroups(s.value.groups); setDraft({}); setError(null); }
      else setError((s.reason as Error).message);
      // Only to offer the tags that exist. A failure here costs the dropdown,
      // not the screen.
      if (i.status === 'fulfilled') {
        setImages([...i.value.releases, ...i.value.builds].map((t) => t.image));
      }
    } catch (e) { setError((e as Error).message); }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  const dirty = useMemo(() => Object.keys(draft), [draft]);

  const value = (d: SettingDef) => draft[d.key] ?? d.effective;
  const set = (d: SettingDef, v: string) =>
    setDraft((prev) => {
      // Typing a value back to what is already in force clears it from the draft,
      // so the Save button counts real changes rather than keystrokes.
      if (v === d.effective) { const { [d.key]: _drop, ...rest } = prev; return rest; }
      return { ...prev, [d.key]: v };
    });

  async function save() {
    setBusy(true); setSaved(null);
    try {
      await api.saveSettings(dirty.map((key) => ({ key, value: draft[key] })));
      await refresh();
      setSaved(`${dirty.length} setting${dirty.length === 1 ? '' : 's'} applied.`);
    } catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }

  async function revert(d: SettingDef) {
    setBusy(true);
    try { await api.revertSetting(d.key); await refresh(); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  }

  function field(d: SettingDef) {
    const v = value(d);
    switch (d.kind) {
      case 'Bool':
        return (
          <Switch checked={v === 'true'} onChange={(e) => set(d, String(e.currentTarget.checked))}
                  label={v === 'true' ? 'On' : 'Off'} />
        );
      case 'Bytes':
        return (
          <NumberInput
            value={Math.round(Number(v || 0) / MB)} min={0} suffix=" MB" w={220}
            onChange={(n) => set(d, String(Math.round(Number(n || 0) * MB)))}
          />
        );
      case 'Int': case 'Seconds': case 'Hours': case 'Days': {
        const suffix = d.kind === 'Seconds' ? ' s' : d.kind === 'Hours' ? ' h' : d.kind === 'Days' ? ' days' : '';
        return (
          <NumberInput value={Number(v || 0)} min={d.min ?? undefined} max={d.max ?? undefined}
                       suffix={suffix} w={220} onChange={(n) => set(d, String(n ?? 0))} />
        );
      }
      case 'Image':
        return (
          <Autocomplete value={v} data={images} w={440} onChange={(s) => set(d, s)}
                        placeholder="registry/repo:tag" />
        );
      default:
        return <TextInput value={v} w={440} onChange={(e) => set(d, e.currentTarget.value)} />;
    }
  }

  if (!groups) {
    return error
      ? <Alert color="red" icon={<IconAlertTriangle size={16} />}>{error}</Alert>
      : <Group p="xl" justify="center"><Loader /></Group>;
  }

  return (
    <Stack gap="md">
      <Group justify="space-between" align="flex-start">
        <div>
          <Title order={3}>Settings</Title>
          <Text size="sm" c="dimmed">
            What can be changed without editing a file and recreating a container
          </Text>
        </div>
        <Button leftSection={<IconDeviceFloppy size={16} />} disabled={dirty.length === 0} loading={busy}
                onClick={() => void save()}>
          Save {dirty.length > 0 && `(${dirty.length})`}
        </Button>
      </Group>

      {error && <Alert color="red" icon={<IconAlertTriangle size={16} />} withCloseButton onClose={() => setError(null)}>{error}</Alert>}
      {saved && <Alert color="green" withCloseButton onClose={() => setSaved(null)}>{saved}</Alert>}

      {/* Said once, here, rather than implied by absence. An operator hunting for
          the SMTP password should find out where it is, not conclude the screen
          is broken. */}
      <Alert color="gray" variant="light">
        Passwords and connection details are deliberately not on this screen —
        credentials for Postgres, Patroni and SMTP, and everything under Access, stay in{' '}
        <Code>cp.env</Code>. A mistake in any of them severs the panel from the cluster or from the
        way in, and neither could then be fixed from here.
      </Alert>

      {groups.map((g) => (
        <Card withBorder padding="md" key={g.group}>
          <Text fw={600} mb="sm">{g.group}</Text>
          <Stack gap="lg">
            {g.settings.map((d) => {
              const src = SOURCE_LABEL[d.source];
              return (
                <Group key={d.key} align="flex-start" justify="space-between" wrap="nowrap" gap="xl">
                  <div style={{ flex: 1, minWidth: 0 }}>
                    <Group gap={6}>
                      <Text size="sm" fw={500}>{d.label}</Text>
                      <Tooltip label={src.hint} withArrow>
                        <Badge size="xs" variant="light" color={src.color}>{src.text}</Badge>
                      </Tooltip>
                      {!d.runtimeEditable && (
                        <Tooltip label="Read once at startup — the panel must be recreated for this to take effect." withArrow>
                          <Badge size="xs" variant="light" color="orange">needs a restart</Badge>
                        </Tooltip>
                      )}
                    </Group>
                    <Text size="xs" c="dimmed" mt={2}>{d.description}</Text>
                    <Text size="xs" c="dimmed" mt={2}><Code fz={10}>{d.key}</Code></Text>
                  </div>
                  <Group gap={6} wrap="nowrap">
                    {field(d)}
                    <Tooltip
                      label={d.source === 'Database'
                        ? `Revert to ${d.configValue ?? d.default}`
                        : 'Nothing to revert — this is not overridden here'}
                      withArrow
                    >
                      <span>
                        <Button variant="subtle" size="xs" color="gray" disabled={d.source !== 'Database'}
                                leftSection={<IconArrowBackUp size={14} />} onClick={() => void revert(d)}>
                          Revert
                        </Button>
                      </span>
                    </Tooltip>
                  </Group>
                </Group>
              );
            })}
          </Stack>
        </Card>
      ))}
    </Stack>
  );
}
