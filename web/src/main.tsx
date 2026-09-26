import { StrictMode, useEffect } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { Button, Center, Loader, MantineProvider, Stack, Text } from '@mantine/core';
import '@mantine/core/styles.css';
import { theme } from './theme';
import './theme.css';
import './person.css';
import App from './App.tsx';
import { Login } from './Login.tsx';
import { AuthProvider, useAuth } from './auth.tsx';

/**
 * Decides what to render from what the SERVER said, never from the URL.
 */
function Root() {
  const { ctx, loading } = useAuth();

  if (loading || !ctx) {
    return <Center h="100vh"><Loader /></Center>;
  }

  if (ctx.authenticated) {
    // The router lives INSIDE the authenticated branch. A signed-out visitor has
    // exactly one destination, and wrapping the login screen in routing would only
    // create URLs that look navigable and are not.
    return (
      <BrowserRouter>
        <App />
      </BrowserRouter>
    );
  }

  if (ctx.localLoginAvailable) return <Login enrolled={ctx.enrolled} />;

  if (ctx.directoryLoginAvailable) return <DirectoryRedirect />;

  return (
    <Center h="100vh">
      <Stack align="center" gap="xs" maw={420}>
        <Text fw={600}>Not signed in</Text>
        <Text size="sm" c="dimmed" ta="center">
          Open the panel from the public address, or tunnel to the break-glass
          listener if login.getzulo.com is down.
        </Text>
      </Stack>
    </Center>
  );
}

function DirectoryRedirect() {
  const failed = new URLSearchParams(window.location.search).get('directory') === 'failed';

  useEffect(() => {
    if (failed) return;
    window.location.assign('/api/auth/directory');
  }, [failed]);

  if (failed) {
    return (
      <Center h="100vh">
        <Stack align="center" gap="sm" maw={420} p="md">
          <Text fw={600}>Could not sign in</Text>
          <Text size="sm" c="dimmed" ta="center">
            login.getzulo.com did not complete the sign-in. Try again, or tunnel
            to the break-glass listener if the directory is down.
          </Text>
          <Button onClick={() => window.location.assign('/api/auth/directory')}>
            Try again
          </Button>
        </Stack>
      </Center>
    );
  }

  return <Center h="100vh"><Loader /></Center>;
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    {/* Light by default rather than following the OS. This is read next to a
        terminal and a Grafana tab, and "auto" made the panel dark on most
        machines — which is a legitimate taste, but not the one the layout was
        drawn for. The toggle in the header keeps the other. */}
    <MantineProvider theme={theme} defaultColorScheme="light">
      <AuthProvider>
        <Root />
      </AuthProvider>
    </MantineProvider>
  </StrictMode>,
);
