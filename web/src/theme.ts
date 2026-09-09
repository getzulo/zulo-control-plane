import { createTheme, type MantineColorsTuple } from '@mantine/core';

/**
 * The blue-lavender the panel is built around — one accent, used sparingly.
 *
 * A ramp rather than a single value because Mantine picks a different step for
 * filled buttons, light backgrounds, borders and text, and letting it derive
 * those from one colour produces a light variant that is either muddy or
 * fluorescent. Index 6 is the accent; 0 is the wash behind an active nav item.
 */
const brand: MantineColorsTuple = [
  '#eef1ff',
  '#dfe3fb',
  '#bcc4f1',
  '#96a3e8',
  '#7787e0',
  '#6474db',
  '#5a6bda',
  '#4a5ac2',
  '#4050ae',
  '#34449a',
];

/**
 * Light, quiet and rounded — the browser-chrome look: a soft neutral canvas,
 * white surfaces, hairline borders and shadows you notice only by their absence.
 *
 * Nearly all of this is `defaultProps` rather than CSS. Setting Card's defaults
 * here restyles every card in the panel at once and keeps each screen free of
 * presentation: the screens say `<Card>`, and what a card looks like is decided
 * in one place.
 */
export const theme = createTheme({
  colors: { brand },
  primaryColor: 'brand',
  primaryShade: { light: 6, dark: 5 },

  // Rounded, but not pill-shaped. Cards go further (below) because a large
  // radius reads as "surface" and a small one as "control".
  defaultRadius: 'md',
  radius: { xs: '4px', sm: '6px', md: '8px', lg: '14px', xl: '20px' },

  // The interface font of whatever this is being read on. No web font: a panel
  // that is opened when something is broken must not wait on a CDN to become
  // legible.
  fontFamily:
    '-apple-system, BlinkMacSystemFont, "Segoe UI Variable Text", "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif',
  fontFamilyMonospace:
    '"Cascadia Code", "JetBrains Mono", ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace',

  headings: {
    fontWeight: '650',
    sizes: {
      h1: { fontSize: '1.6rem', lineHeight: '1.3' },
      h2: { fontSize: '1.35rem', lineHeight: '1.3' },
      h3: { fontSize: '1.15rem', lineHeight: '1.35' },
      h4: { fontSize: '1rem', lineHeight: '1.4' },
    },
  },

  // Barely-there elevation. The border does the separating; the shadow only
  // lifts a surface off the canvas enough to be believable.
  shadows: {
    xs: '0 1px 2px rgba(16, 24, 40, 0.04), 0 1px 3px rgba(16, 24, 40, 0.04)',
    sm: '0 1px 3px rgba(16, 24, 40, 0.05), 0 4px 8px -2px rgba(16, 24, 40, 0.04)',
    md: '0 4px 8px -2px rgba(16, 24, 40, 0.06), 0 12px 20px -4px rgba(16, 24, 40, 0.06)',
    lg: '0 8px 16px -4px rgba(16, 24, 40, 0.08), 0 20px 32px -8px rgba(16, 24, 40, 0.08)',
  },

  components: {
    Card: {
      defaultProps: {
        withBorder: true,
        shadow: 'xs',
        radius: 'lg',
        padding: 'lg',
      },
    },
    Paper: { defaultProps: { radius: 'lg' } },
    Modal: { defaultProps: { radius: 'lg', centered: true, overlayProps: { blur: 2 } } },

    Button: { defaultProps: { radius: 'md' } },
    ActionIcon: { defaultProps: { radius: 'md' } },
    // Light by default: a screen where every action is a filled accent button has
    // no emphasis left for the one action that matters.
    //
    // The upper-casing is turned off in theme.css rather than here — Mantine puts
    // it on the badge ROOT class, and a rule is something that can be verified in
    // the emitted stylesheet. A caller passing tt= still wins, being inline.
    Badge: { defaultProps: { radius: 'sm', variant: 'light' } },

    TextInput: { defaultProps: { radius: 'md' } },
    NumberInput: { defaultProps: { radius: 'md' } },
    PasswordInput: { defaultProps: { radius: 'md' } },
    Select: { defaultProps: { radius: 'md' } },
    Autocomplete: { defaultProps: { radius: 'md' } },

    // The pill in the sidebar.
    NavLink: { defaultProps: { radius: 'md' } },

    Table: { defaultProps: { verticalSpacing: 'sm', horizontalSpacing: 'md' } },
    Tooltip: { defaultProps: { radius: 'sm', withArrow: true } },
    Alert: { defaultProps: { radius: 'md', variant: 'light' } },
    Progress: { defaultProps: { radius: 'xl' } },
    Loader: { defaultProps: { type: 'dots' } },
  },
});
