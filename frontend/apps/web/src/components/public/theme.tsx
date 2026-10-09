/** Revalidate the whole anonymous projection; only fixed color slots reach CSS. */
export function publicThemeCss(theme: unknown): string | null {
  if (typeof theme !== 'object' || theme === null || Array.isArray(theme)) return null;
  const tokens = theme as Record<string, unknown>;
  const names = ['primary', 'background', 'foreground', 'muted'] as const;
  if (
    Object.keys(tokens).length !== names.length ||
    !names.every((name) => typeof tokens[name] === 'string' && /^#[0-9a-f]{6}$/.test(tokens[name]))
  )
    return null;
  return `:root{--ls-primary:${tokens.primary};--ls-bg:${tokens.background};--ls-fg:${tokens.foreground};--ls-muted:${tokens.muted};}`;
}

export function PublicTheme({ theme }: { readonly theme: unknown }) {
  const css = publicThemeCss(theme);
  return css === null ? null : <style>{css}</style>;
}
