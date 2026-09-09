import { useState } from 'react';
import {
  Alert, Anchor, Button, Card, Code, PasswordInput, PinInput, Stack, Text, TextInput, Title,
} from '@mantine/core';
import { api, token } from './api';
import { useAuth } from './auth';
import { BuildStamp } from './shared';

/**
 * The break-glass sign-in. Only ever rendered when the SERVER said this request
 * arrived on the loopback listener — the public listener answers 404 for these
 * endpoints, so showing the form there would be a lie.
 */
export function Login({ enrolled }: { enrolled: boolean }) {
  const { refresh } = useAuth();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Enrolment state, used only when the account has no second factor yet.
  const [secret, setSecret] = useState<string | null>(null);
  const [uri, setUri] = useState<string | null>(null);

  async function signIn() {
    setBusy(true); setError(null);
    try {
      const result = await api.login(email, password, code);
      token.set(result.token);
      await refresh();
    } catch (e) {
      // The API answers every failure identically on purpose — a message that
      // distinguished "wrong password" from "locked" would be an oracle. Say so,
      // so a locked-out operator does not read this as a broken panel.
      setError(e instanceof Error ? e.message : 'Sign-in failed');
      setCode('');
    } finally { setBusy(false); }
  }

  async function begin() {
    setBusy(true); setError(null);
    try {
      const result = await api.enrolBegin(email, password);
      setSecret(result.secret);
      setUri(result.uri);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not start enrolment');
    } finally { setBusy(false); }
  }

  async function confirm() {
    if (!secret) return;
    setBusy(true); setError(null);
    try {
      await api.enrolConfirm(email, password, secret, code);
      setSecret(null); setUri(null); setCode('');
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'That code did not match');
      setCode('');
    } finally { setBusy(false); }
  }

  const enrolling = !enrolled;

  return (
    <Card withBorder shadow="sm" padding="lg" maw={420} mx="auto" mt={80}>
      <Stack gap="md">
        <div>
          <Title order={3}>ZuloOne control plane</Title>
          <Text size="sm" c="dimmed">
            {enrolling
              ? 'This account has no second factor yet. Enrol one to sign in.'
              : 'Break-glass sign-in — you are reached through a tunnel, not through Cloudflare.'}
          </Text>
        </div>

        {error && <Alert color="red" variant="light">{error}</Alert>}

        <TextInput label="E-mail" value={email} onChange={(e) => setEmail(e.currentTarget.value)}
                   autoComplete="username" disabled={busy || !!secret} />
        <PasswordInput label="Password" value={password} onChange={(e) => setPassword(e.currentTarget.value)}
                       autoComplete="current-password" disabled={busy || !!secret} />

        {enrolling && !secret && (
          <Button onClick={() => void begin()} loading={busy} disabled={!email || !password}>
            Start enrolment
          </Button>
        )}

        {secret && (
          <Stack gap="xs">
            <Text size="sm">
              Add this to your authenticator, then enter the code it shows. The secret is
              not saved until a code from it verifies — a half-scanned code cannot lock
              you out.
            </Text>
            <Code block>{secret}</Code>
            {uri && <Anchor href={uri} size="xs">Open in an authenticator app</Anchor>}
          </Stack>
        )}

        {(!enrolling || secret) && (
          <Stack gap="xs">
            <Text size="sm" fw={500}>Six-digit code</Text>
            <PinInput length={6} type="number" oneTimeCode value={code} onChange={setCode} disabled={busy} />
          </Stack>
        )}

        {!enrolling && (
          <Button onClick={() => void signIn()} loading={busy}
                  disabled={!email || !password || code.length !== 6}>
            Sign in
          </Button>
        )}

        {secret && (
          <Button onClick={() => void confirm()} loading={busy} disabled={code.length !== 6}>
            Confirm enrolment
          </Button>
        )}

        {/* The one screen where the operator is most likely to be reporting a
            problem and least able to find the version any other way. */}
        <BuildStamp />
      </Stack>
    </Card>
  );
}
