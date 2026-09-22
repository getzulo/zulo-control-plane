import { StrictMode, useCallback, useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import {
  AppShell, Button, Center, Container, Group, Loader, MantineProvider, Stack, Text, Title,
} from '@mantine/core';
import '@mantine/core/styles.css';
import { theme } from '../theme';
import '../theme.css';
import { portal, token } from './api';
import { PortalAuth, TokenScreen } from './Auth';
import { StandDetail, StandList } from './Stands';
import type { Stand } from './api';

/**
 * The customer portal: a SECOND entry point served by the same container as the
 * operator dashboard, at /portal.
 *
 * Separate from the dashboard's bundle, not a route inside it. The two have
 * different audiences, different credentials and different blast radii, and a
 * shared bundle would ship the fleet screens — every tenant's slug, the image
 * tags, the node names — to every customer's browser, where "the router never
 * shows it" is the only thing standing between them and it.
 */
function Root() {
  const [ready, setReady] = useState(false);
  const [signedIn, setSignedIn] = useState(false);
  const [email, setEmail] = useState<string | null>(null);
  const [open, setOpen] = useState<Stand | null>(null);
  const [off, setOff] = useState(false);

  const refresh = useCallback(async () => {
    try {
      const ctx = await portal.context();
      setSignedIn(ctx.authenticated);
      setEmail(ctx.email);
    } catch (e) {
      // /context answers 404 when Portal:Enabled is false. That is a deployment
      // state, not a fault, and saying so beats a spinner that never resolves.
      if (e && typeof e === 'object' && 'status' in e && (e as { status: number }).status === 404) {
        setOff(true);
      }
      setSignedIn(false);
    } finally {
      setReady(true);
    }
  }, []);

  useEffect(() => { void refresh(); }, [refresh]);

  // The links in e-mail, handled before anything else: they carry a token and
  // must work whether or not there is a session in this browser.
  const path = window.location.pathname.replace(/\/+$/, '');
  if (path === '/portal/verify' || path === '/portal/reset') {
    return (
      <TokenScreen
        kind={path.endsWith('verify') ? 'verify' : 'reset'}
        onDone={() => { void refresh(); }} />
    );
  }

  if (!ready) return <Center h="100vh"><Loader /></Center>;

  if (off) {
    return (
      <Center h="100vh" p="md">
        <Stack align="center" gap="xs" maw={420}>
          <Text fw={600}>Not available</Text>
          <Text size="sm" c="dimmed" ta="center">
            The customer portal is not switched on for this deployment.
          </Text>
        </Stack>
      </Center>
    );
  }

  if (!signedIn) return <PortalAuth onSignedIn={() => { void refresh(); }} />;

  const signOut = async () => {
    try { await portal.logout(); } catch { /* the session may already be gone */ }
    token.set(null);
    setOpen(null);
    void refresh();
  };

  return (
    <AppShell header={{ height: 56 }} padding="md">
      <AppShell.Header>
        <Group h="100%" px="md" justify="space-between">
          <Title order={5}>ZuloOne</Title>
          <Group gap="sm">
            <Text size="sm" c="dimmed">{email}</Text>
            <Button size="xs" variant="subtle" onClick={() => void signOut()}>Sign out</Button>
          </Group>
        </Group>
      </AppShell.Header>

      <AppShell.Main>
        <Container size="md">
          {open
            ? <StandDetail id={open.id} onBack={() => setOpen(null)} />
            : <StandList onOpen={setOpen} />}
        </Container>
      </AppShell.Main>
    </AppShell>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <MantineProvider theme={theme} defaultColorScheme="light">
      <Root />
    </MantineProvider>
  </StrictMode>,
);
