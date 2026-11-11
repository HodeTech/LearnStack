/** The finite form transport preserves values independently of browser newline normalization. */
export type CustomFieldTextMode = 'plain' | 'escaped';
export const normalizeCustomFieldEditorLines = (source: string): string =>
  source.replace(/\r\n?/g, '\n');
export const customFieldTextMode = (value: string): CustomFieldTextMode =>
  value.includes('\r') ? 'escaped' : 'plain';
export function encodeCustomFieldText(value: string, mode: CustomFieldTextMode): string {
  return mode === 'plain'
    ? value
    : value.replace(/\\/g, '\\\\').replace(/\r/g, '\\r').replace(/\n/g, '\\n');
}
export function decodeCustomFieldText(source: string, mode: string): string {
  const text = normalizeCustomFieldEditorLines(source);
  if (mode === 'plain') return text;
  if (mode !== 'escaped') throw new Error('Text entry mode is invalid');
  let result = '';
  for (let index = 0; index < text.length; ++index) {
    const character = text[index]!;
    if (character !== '\\') {
      result += character;
      continue;
    }
    const escaped = text[++index];
    if (escaped === '\\') result += '\\';
    else if (escaped === 'r') result += '\r';
    else if (escaped === 'n') result += '\n';
    else throw new Error('Text escape is invalid');
  }
  return result;
}
export function customFieldChoiceToken(index: number): string {
  if (!Number.isSafeInteger(index) || index < 0 || index >= 20)
    throw new Error('Choice is invalid');
  return 'choice-' + index;
}
export function decodeCustomFieldChoice(token: string, choices: readonly string[]): string {
  const match = /^choice-(0|[1-9][0-9]*)$/.exec(token);
  if (!match || match[0] !== token) throw new Error('Choice is invalid');
  const index = Number(match[1]);
  const value = choices[index];
  if (index >= 20 || value === undefined) throw new Error('Choice is invalid');
  return value;
}
