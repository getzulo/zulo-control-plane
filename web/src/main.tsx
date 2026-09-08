import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { Center, Loader, MantineProvider, Stack, Text } from '@mantine/core';
import '@mantine/core/styles.css';
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

  if (ctx.authenticated) return <App />;

  if (ctx.localLoginAvailable) return <Login enrolled={ctx.enrolled} />;

  // Not authenticated and no local login on this listener. Behind Cloudflare that
  // means the Access session is gone, and only Cloudflare can issue a new one —
  // there is deliberately no password form on the public listener to fall back to.
  return (
    <Center h="100vh">
      <Stack align="center" gap="xs" maw={420}>
        <Text fw={600}>Not signed in</Text>
        <Text size="sm" c="dimmed" ta="center">
          This panel is reached through Cloudflare Access. If you are seeing this,
          the Access session has expired or is not configured — reload to let
          Cloudflare re-issue it.
        </Text>
        <Text size="xs" c="dimmed" ta="center">
          For break-glass access when Cloudflare is unavailable, tunnel to the
          control plane host and use the loopback listener.
        </Text>
      </Stack>
    </Center>
  );
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <MantineProvider defaultColorScheme="auto">
      <AuthProvider>
        <Root />
      </AuthProvider>
    </MantineProvider>
  </StrictMode>,
);
