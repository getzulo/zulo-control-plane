import { NavLink as RouterNavLink, Route, Routes, useLocation } from 'react-router-dom';
import {
  ActionIcon, AppShell, Badge, Burger, Group, NavLink, ScrollArea, Text, ThemeIcon, Tooltip,
  UnstyledButton, useMantineColorScheme,
} from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import {
  IconActivity, IconCloudComputing, IconDatabaseExport, IconLayoutDashboard, IconLogout, IconMoon,
  IconPackages, IconServer2, IconSettings, IconSun,
} from '@tabler/icons-react';
import { useAuth } from './auth';
import { BuildStamp } from './shared';
import { OverviewPage, ActivityPage } from './Overview';
import { TenantsPage } from './Tenants';
import { TenantPage } from './Tenant';
import { InfrastructurePage } from './Infrastructure';
import { SnapshotsPage } from './Snapshots';
import { ImagesPage } from './Images';
import { SettingsPage } from './Settings';

const NAV = [
  { to: '/', label: 'Overview', icon: IconLayoutDashboard, end: true },
  { to: '/tenants', label: 'Tenants', icon: IconCloudComputing, end: false },
  { to: '/infrastructure', label: 'Infrastructure', icon: IconServer2, end: false },
  { to: '/backups', label: 'Backups', icon: IconDatabaseExport, end: false },
  { to: '/images', label: 'Images', icon: IconPackages, end: false },
  { to: '/activity', label: 'Activity', icon: IconActivity, end: false },
  { to: '/settings', label: 'Settings', icon: IconSettings, end: false },
];

/**
 * Light is the default, but an ops panel gets read at 3am too. The choice is
 * Mantine's own, so it persists and applies before first paint.
 */
function ColorSchemeToggle() {
  const { colorScheme, toggleColorScheme } = useMantineColorScheme();
  const dark = colorScheme === 'dark';
  return (
    <Tooltip label={dark ? 'Light' : 'Dark'}>
      <ActionIcon variant="subtle" color="gray" onClick={toggleColorScheme} aria-label="Toggle colour scheme">
        {dark ? <IconSun size={17} /> : <IconMoon size={17} />}
      </ActionIcon>
    </Tooltip>
  );
}

export default function App() {  const auth = useAuth();
  const [opened, { toggle, close }] = useDisclosure();
  const location = useLocation();

  return (
    <AppShell
      header={{ height: 56 }}
      navbar={{ width: 232, breakpoint: 'sm', collapsed: { mobile: !opened } }}
      padding="lg"
    >
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Group gap="sm">
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" />
            <ThemeIcon size={26} radius="md" variant="light"><IconCloudComputing size={16} /></ThemeIcon>
            <Text fw={650} style={{ letterSpacing: '-0.01em' }}>ZuloOne</Text>
            <Badge size="sm">control plane</Badge>
          </Group>
          <Group gap="xs">
            {/* Which door you came through matters when something is wrong: over the
                tunnel Cloudflare is bypassed entirely, and that is worth seeing. */}
            {auth.ctx && (
              <Text size="xs" c="dimmed">
                {auth.ctx.email ?? 'signed in'} · {auth.ctx.mode === 'access' ? 'Cloudflare Access' : 'break-glass'}
              </Text>
            )}
            <ColorSchemeToggle />
            {auth.ctx?.mode === 'local' && (
              <UnstyledButton onClick={() => void auth.signOut()} title="Sign out">
                <IconLogout size={18} />
              </UnstyledButton>
            )}
          </Group>
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="xs">
        {/* grow + a separate section below it, so the stamp sits on the FLOOR of
            the sidebar rather than immediately under the last link. Inside the
            ScrollArea it just trailed the nav, which on a six-item menu left it
            stranded in the middle of an empty column. */}
        <AppShell.Section grow component={ScrollArea}>
          {NAV.map((n) => {
            const active = n.end
              ? location.pathname === n.to
              : location.pathname.startsWith(n.to);
            return (
              <NavLink
                key={n.to}
                component={RouterNavLink}
                to={n.to}
                label={n.label}
                leftSection={<n.icon size={18} />}
                active={active}
                onClick={close}
              />
            );
          })}
        </AppShell.Section>
        <AppShell.Section pt="xs" px={6}>
          {/* On every screen. A bug report that names a screen but not a build is
              a bug report nobody can act on, and asking for it after the fact
              never works. */}
          <BuildStamp />
        </AppShell.Section>
      </AppShell.Navbar>
      <AppShell.Main>
        <Routes>
          <Route path="/" element={<OverviewPage />} />
          <Route path="/tenants" element={<TenantsPage />} />
          <Route path="/tenants/:id" element={<TenantPage />} />
          <Route path="/infrastructure" element={<InfrastructurePage />} />
          <Route path="/backups" element={<SnapshotsPage />} />
          <Route path="/images" element={<ImagesPage />} />
          <Route path="/activity" element={<ActivityPage />} />
          <Route path="/settings" element={<SettingsPage />} />
          {/* Anything else is a stale bookmark; the overview is always safe. */}
          <Route path="*" element={<OverviewPage />} />
        </Routes>
      </AppShell.Main>
    </AppShell>
  );
}
