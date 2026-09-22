import { useState } from 'react';
import {
  Alert, Anchor, Button, Card, Center, Code, PasswordInput, Stack, Text, TextInput, Title,
} from '@mantine/core';
import { ApiError, portal, token } from './api';

type Screen = 'signIn' | 'register' | 'forgot';

/**
 * Everything a signed-out visitor can reach.
 *
 * One component rather than routes, for the same reason the operator dashboard
 * puts its login outside the router: a signed-out visitor has a handful of
 * destinations and no state worth a URL. The two screens that DO need a URL —
 * the links in e-mail — are handled before this renders, in main.tsx, because
 * they arrive carrying a token in the query string.
 */
export function PortalAuth({ onSignedIn }: { onSignedIn: () => void }) {
  const [screen, setScreen] = useState<Screen>('signIn');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [totp, setTotp] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  /** The server asked for a second factor. Only then is the field shown. */
  const [needsTotp, setNeedsTotp] = useState(false);

  const run = async (action: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await action();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Something went wrong. Try again.');
    } finally {
      setBusy(false);
    }
  };

  const signIn = () => run(async () => {
    try {
      const result = await portal.login({ email, password, totp: totp || undefined });
      token.set(result.token);
      onSignedIn();
    } catch (e) {
      // A wrong or missing code answers 401 like a wrong password does — on
      // purpose, so the form is not an oracle. Showing the field once a sign-in
      // has failed is the honest compromise: it costs a second attempt and tells
      // a stranger nothing.
      if (e instanceof ApiError && e.status === 401 && !needsTotp) setNeedsTotp(true);
      throw e;
    }
  });

  const register = () => run(async () => {
    const result = await portal.register({
      email,
      password,
      displayName: displayName || undefined,
      locale: navigator.language?.slice(0, 5),
    });
    setNotice(result.message);
    setScreen('signIn');
  });

  const forgot = () => run(async () => {
    const result = await portal.forgot(email);
    setNotice(result.message);
    setScreen('signIn');
  });

  return (
    <Center h="100vh" p="md">
      <Card withBorder shadow="sm" padding="lg" radius="md" w={420}>
        <Stack gap="md">
          <div>
            <Title order={3}>
              {screen === 'signIn' && 'Sign in'}
              {screen === 'register' && 'Create an account'}
              {screen === 'forgot' && 'Reset your password'}
            </Title>
            <Text size="sm" c="dimmed">Your ZuloOne stands.</Text>
          </div>

          {error && <Alert color="red" variant="light">{error}</Alert>}
          {notice && <Alert color="blue" variant="light">{notice}</Alert>}

          {screen === 'register' && (
            <TextInput
              label="Your name" value={displayName} disabled={busy}
              onChange={(e) => setDisplayName(e.currentTarget.value)} />
          )}

          <TextInput
            label="E-mail" type="email" value={email} disabled={busy} autoComplete="username"
            onChange={(e) => setEmail(e.currentTarget.value)} />

          {screen !== 'forgot' && (
            <PasswordInput
              label="Password" value={password} disabled={busy}
              autoComplete={screen === 'register' ? 'new-password' : 'current-password'}
              description={screen === 'register' ? 'At least 12 characters.' : undefined}
              onChange={(e) => setPassword(e.currentTarget.value)} />
          )}

          {screen === 'signIn' && needsTotp && (
            <TextInput
              label="Authenticator code" value={totp} disabled={busy}
              inputMode="numeric" autoComplete="one-time-code"
              description="If your account has a second factor switched on."
              onChange={(e) => setTotp(e.currentTarget.value)} />
          )}

          <Button
            loading={busy}
            onClick={() => {
              if (screen === 'signIn') void signIn();
              else if (screen === 'register') void register();
              else void forgot();
            }}>
            {screen === 'signIn' && 'Sign in'}
            {screen === 'register' && 'Create account'}
            {screen === 'forgot' && 'Send me a link'}
          </Button>

          <Stack gap={4}>
            {screen !== 'signIn' && (
              <Anchor size="sm" onClick={() => { setScreen('signIn'); setError(null); }}>
                Back to signing in
              </Anchor>
            )}
            {screen === 'signIn' && (
              <>
                <Anchor size="sm" onClick={() => { setScreen('register'); setError(null); }}>
                  I do not have an account yet
                </Anchor>
                <Anchor size="sm" onClick={() => { setScreen('forgot'); setError(null); }}>
                  I have forgotten my password
                </Anchor>
              </>
            )}
          </Stack>
        </Stack>
      </Card>
    </Center>
  );
}

/**
 * The two screens reached from a link in an e-mail. Both take their token from
 * the query string, use it once, and then send the person back to a clean URL —
 * so a reload does not retry a token that has already been spent, and the
 * browser's history does not keep a credential in it.
 */
export function TokenScreen({ kind, onDone }: { kind: 'verify' | 'reset'; onDone: () => void }) {
  const tokenFromUrl = new URLSearchParams(window.location.search).get('token') ?? '';
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const finish = () => {
    window.history.replaceState(null, '', '/portal');
    onDone();
  };

  const run = async () => {
    setBusy(true);
    setError(null);
    try {
      if (kind === 'verify') {
        const result = await portal.verify(tokenFromUrl);
        setDone(result.claimed > 0
          ? `Your address is confirmed, and ${result.claimed} stand${result.claimed === 1 ? '' : 's'} ${result.claimed === 1 ? 'is' : 'are'} now yours. Sign in to see ${result.claimed === 1 ? 'it' : 'them'}.`
          : 'Your address is confirmed. Sign in to carry on.');
      } else {
        await portal.reset(tokenFromUrl, password);
        setDone('Your password is set. Sign in with it.');
      }
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'That did not work.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <Center h="100vh" p="md">
      <Card withBorder shadow="sm" padding="lg" radius="md" w={420}>
        <Stack gap="md">
          <Title order={3}>{kind === 'verify' ? 'Confirm your address' : 'Set a new password'}</Title>

          {!tokenFromUrl && (
            <Alert color="red" variant="light">
              This link is missing its token. Open the one from the e-mail exactly as it was sent —
              some mail clients cut long links in half.
            </Alert>
          )}

          {error && <Alert color="red" variant="light">{error}</Alert>}

          {done ? (
            <>
              <Alert color="green" variant="light">{done}</Alert>
              <Button onClick={finish}>Continue</Button>
            </>
          ) : (
            <>
              {kind === 'reset' && (
                <PasswordInput
                  label="New password" value={password} disabled={busy} autoComplete="new-password"
                  description="At least 12 characters."
                  onChange={(e) => setPassword(e.currentTarget.value)} />
              )}
              <Button loading={busy} disabled={!tokenFromUrl} onClick={() => void run()}>
                {kind === 'verify' ? 'Confirm' : 'Set password'}
              </Button>
              {kind === 'reset' && (
                <Text size="xs" c="dimmed">
                  Setting a password also signs out every other browser that was using this account.
                </Text>
              )}
            </>
          )}
        </Stack>
      </Card>
    </Center>
  );
}

/** Shown once, and never fetchable again — so it has to be obviously copyable. */
export function OneTimeSecret({ label, value }: { label: string; value: string }) {
  return (
    <Alert color="yellow" variant="light" title={label}>
      <Stack gap={6}>
        <Code style={{ fontSize: 16, userSelect: 'all' }}>{value}</Code>
        <Text size="xs">
          Shown once. Copy it now — closing this is the only copy gone.
        </Text>
      </Stack>
    </Alert>
  );
}
