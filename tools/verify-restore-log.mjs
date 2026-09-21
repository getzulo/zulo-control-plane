/**
 * B4 acceptance: the Restore job's log must be byte-identical to what it was
 * before the refactor.
 *
 * Compares the SUCCESS path and the ROLLBACK path separately — in a run they
 * are mutually exclusive, so concatenating them in source order would report
 * a reordering that can never happen.
 *
 * Interpolation holes are normalised to {} and their expressions printed, so
 * that a renamed variable is visible as a rename and can be checked against
 * the call site rather than silently accepted or silently failed.
 */
import { execSync } from 'node:child_process';
import { readFileSync } from 'node:fs';

const REPO = 'd:/Sources/zulo-control-plane';
const HANDLER = 'src/ZuloOne.ControlPlane/Jobs/RestoreJobHandler.cs';
const SERVICE = 'src/ZuloOne.ControlPlane/Provisioning/TenantCloneService.cs';

function emissions(src) {
  const out = [];
  const re = /\b(StepAsync|LogAsync)\s*\(\s*([\s\S]*?)(?:,\s*(\d+))?\s*,\s*(?:ct|CancellationToken\.None)\s*\)/g;
  let m;
  while ((m = re.exec(src)) !== null) {
    const [, kind, rawArg, progress] = m;
    const holes = [];
    const text = rawArg
      .replace(/^\s*\$?@?"/, '')
      .replace(/"\s*$/, '')
      .replace(/"\s*\+\s*\n?\s*\$?"/g, '')
      .replace(/\{([^{}]+)\}/g, (_, h) => { holes.push(h.trim()); return '{}'; })
      .replace(/\s+/g, ' ')
      .trim();
    out.push({ key: `${kind}${progress ? `(${progress})` : ''}: ${text}`, holes, at: m.index });
  }
  return out;
}

/** Split at the handler's/service's catch block: before it runs on success. */
function split(src) {
  const i = src.indexOf('\n        catch');
  const cut = i === -1 ? src.length : i;
  return { success: emissions(src.slice(0, cut)), rollback: emissions(src.slice(cut)) };
}

const oldSrc = execSync(`git -C "${REPO}" show HEAD:${HANDLER}`, { encoding: 'utf8', maxBuffer: 1e8 });
const newHandler = readFileSync(`${REPO}/${HANDLER}`, 'utf8');
const newService = readFileSync(`${REPO}/${SERVICE}`, 'utf8');

const before = split(oldSrc);
const svc = split(newService);
const callIdx = newHandler.indexOf('_clone.CloneAsync(');
const after = {
  success: [
    ...emissions(newHandler.slice(0, callIdx)),
    ...svc.success,
    ...emissions(newHandler.slice(callIdx)),
  ],
  rollback: svc.rollback,
};

let ok = true;
const renames = [];
for (const path of ['success', 'rollback']) {
  const a = before[path], b = after[path];
  console.log(`\n── ${path} path — before ${a.length}, after ${b.length}`);
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const x = a[i], y = b[i];
    if (x?.key === y?.key) {
      const r = (x.holes || []).map((h, j) => [h, y.holes[j]]).filter(([p, q]) => p !== q);
      console.log(`  ok   ${x.key}`);
      for (const [p, q] of r) { renames.push([p, q]); console.log(`         hole: ${p}  →  ${q}`); }
      continue;
    }
    // A literal that became an interpolation renders the same iff the hole is
    // bound to that literal at the call site. Accept only in that case.
    const lit = x?.key.match(/^LogAsync: (.*)$/)?.[1];
    const tpl = y?.key.match(/^LogAsync: (.*)$/)?.[1];
    if (lit && tpl && tpl.includes('{}')) {
      const re = new RegExp('^' + tpl.replace(/[.*+?^${}()|[\]\\]/g, '\\$&').replace(/\\\{\\\}/g, '(.*)') + '$');
      const m = lit.match(re);
      if (m) {
        renames.push([`"${m[1]}"`, y.holes[0]]);
        console.log(`  ok   ${tpl}\n         hole: literal "${m[1]}"  →  ${y.holes[0]}`);
        continue;
      }
    }
    ok = false;
    console.log(`  DIFF before: ${x?.key ?? '(none)'}\n       after : ${y?.key ?? '(none)'}`);
  }
}

// Every renamed hole must provably carry the same value. Resolved from the
// handler: the CloneAsync arguments and the Tenant initialiser.
console.log('\n── hole bindings at the call site');
const call = newHandler.slice(newHandler.indexOf('_clone.CloneAsync('));
const args = call.slice(call.indexOf('(') + 1, call.indexOf(');')).split(',').map(s => s.trim());
const bindings = {
  'snapshotSizeBytes / 1024': ['snapshot.SizeBytes / 1024', args[3] === 'snapshot.SizeBytes'],
  '_containers.HostFor(tenant.Slug)': ['_containers.HostFor(slug)', /Slug\s*=\s*slug\b/.test(newHandler)],
  rollbackNoun: ['"scratch tenant"', args[4] === '"scratch tenant"'],
};
for (const [hole, [was, proven]] of Object.entries(bindings)) {
  if (!renames.some(([, q]) => q === hole)) continue;
  console.log(`  ${proven ? 'ok  ' : 'FAIL'} ${hole} === ${was}`);
  if (!proven) ok = false;
}

console.log(ok
  ? '\nThe Restore log is unchanged: same order, same text, every renamed hole\nbound to the same value at the call site.'
  : '\nDIFFERS — acceptance not met.');
process.exit(ok ? 0 : 1);
