import type { Config } from 'tailwindcss';

/**
 * Shared Tailwind preset. Tenant theme tokens are layered on top at the app's
 * layout level via CSS custom properties (`--ls-primary`, `--ls-bg`, …).
 * See standards 07 § Tenant Branding.
 */
export const learnstackTailwindPreset = {
  theme: {
    extend: {
      colors: {
        'ls-primary': 'var(--ls-primary, #1f6feb)',
        'ls-bg': 'var(--ls-bg, #ffffff)',
        'ls-fg': 'var(--ls-fg, #0f172a)',
        'ls-muted': 'var(--ls-muted, #64748b)',
      },
      fontFamily: {
        sans: ['var(--ls-font-sans, ui-sans-serif)', 'system-ui', 'sans-serif'],
      },
    },
  },
} satisfies Partial<Config>;
