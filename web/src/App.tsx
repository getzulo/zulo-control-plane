import { NavLink as RouterNavLink, Route, Routes, useLocation } from 'react-router-dom';
import { AppShell, Badge, Burger, Group, NavLink, ScrollArea, Text, UnstyledButton } from '@mantine/core';
import { useDisclosure } from '@mantine/hooks';
import {
  IconActivity, IconCloudComputing, IconDatabaseExport, IconLayoutDashboard, IconLogout, IconPackages, IconServer2,
} from '@tabler/icons-react';
import { useAuth } from './auth';
import { OverviewPage, ActivityPage } from './Overview';
import { TenantsPage } from './Tenants';
import { TenantPage } from './Tenant';
import { InfrastructurePage } from './Infrastructure';
import { SnapshotsPage } from './Snapshots';
import { ImagesPage } from './Images';

const NAV = [
  { to: '/', label: 'Overview', icon: IconLayoutDashboard, end: true },
  { to: '/tenants', label: 'Tenants', icon: IconCloudComputing, end: false },
  { to: '/infrastructure', label: 'Infrastructure', icon: IconServer2, end: false },
  { to: '/backups', label: 'Backups', icon: IconDatabaseExport, end: false },
  { to: '/images', label: 'Images', icon: IconPackages, end: false },
  { to: '/activity', label: 'Activity', icon: IconActivity, end: false },
];

export default function App() {
  const auth = useAuth();
  const [opened, { toggle, close }] = useDisclosure();
  const location = useLocation();

  return (
    <AppShell
      header={{ height: 52 }}
      navbar={{ width: 220, breakpoint: 'sm', collapsed: { mobile: !opened } }}
      padding="md"
    >
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Group gap="sm">
            <Burger opened={opened} onClick={toggle} hiddenFrom="sm" size="sm" />
            <Text fw={700}>ZuloOne</Text>
            <Badge variant="light" size="sm">control plane</Badge>
          </Group>
          <Group gap="sm">
            {/* Which door you came through matters when something is wrong: over the
                tunnel Cloudflare is bypassed entirely, and that is worth seeing. */}
            {auth.ctx && (
              <Text size="xs" c="dimmed">
                {auth.ctx.email ?? 'signed in'} · {auth.ctx.mode === 'access' ? 'Cloudflare Access' : 'break-glass'}
              </Text>
            )}
            {auth.ctx?.mode === 'local' && (
              <UnstyledButton onClick={() => void auth.signOut()} title="Sign out">
                <IconLogout size={18} />
              </UnstyledButton>
            )}
          </Group>
        </Group>
      </AppShell.Header>

      <AppShell.Navbar p="xs">
        <ScrollArea>
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
        </ScrollArea>
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
          {/* Anything else is a stale bookmark; the overview is always safe. */}
          <Route path="*" element={<OverviewPage />} />
        </Routes>
      </AppShell.Main>
    </AppShell>
  );
}
