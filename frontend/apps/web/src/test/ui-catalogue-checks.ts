import { isStructurallySame, parse, TYPE } from '@formatjs/icu-messageformat-parser';
import type { MessageFormatElement } from '@formatjs/icu-messageformat-parser';
import ts from 'typescript';

export type Catalogue = Record<string, unknown>;

function flatten(catalogue: Catalogue, prefix = ''): Map<string, string> {
  const entries = new Map<string, string>();
  for (const [segment, value] of Object.entries(catalogue)) {
    if (!/^[a-z][a-z0-9_]*$/.test(segment) || segment.startsWith('lockey_')) {
      throw new Error('Invalid UI key grammar');
    }
    const key = prefix ? `${prefix}.${segment}` : segment;
    if (typeof value === 'string' && value.trim()) {
      entries.set(key, value);
    } else if (value !== null && typeof value === 'object' && !Array.isArray(value)) {
      const nested = flatten(value as Catalogue, key);
      if (!nested.size) throw new Error('Empty catalogue group');
      for (const [nestedKey, message] of nested) entries.set(nestedKey, message);
    } else {
      throw new Error('Empty or invalid UI message');
    }
  }
  return entries;
}

function assertPlainMessages(elements: MessageFormatElement[]): void {
  for (const element of elements) {
    if (element.type === TYPE.tag) throw new Error('Rich UI messages are forbidden');
    if (element.type === TYPE.plural || element.type === TYPE.select) {
      for (const option of Object.values(element.options)) assertPlainMessages(option.value);
    }
  }
}

export function checkCatalogues(catalogues: Catalogue[], usedKeys: readonly string[]): void {
  const flattened = catalogues.map((catalogue) => flatten(catalogue));
  const reference = flattened[0];
  if (!reference?.size) throw new Error('UI catalogues must be nonempty');
  const referenceKeys = [...reference.keys()].sort();
  for (const catalogue of flattened) {
    if (JSON.stringify([...catalogue.keys()].sort()) !== JSON.stringify(referenceKeys)) {
      throw new Error('UI catalogue keys differ');
    }
    for (const [key, message] of catalogue) {
      const parsed = parse(message);
      assertPlainMessages(parsed);
      const expected = parse(reference.get(key)!);
      if (!isStructurallySame(expected, parsed).success) {
        throw new Error('UI message arguments differ');
      }
    }
    for (const key of usedKeys) {
      if (!catalogue.has(key)) throw new Error('Used UI key is missing');
    }
  }
}

/** Bounded census of direct calls, simple aliases and literal Promise.all tuples. */
export function publicCallsiteKeys(
  source: string,
  filename = 'consumer.tsx',
  property = 't',
): string[] {
  const file = ts.createSourceFile(
    filename,
    source,
    ts.ScriptTarget.Latest,
    true,
    ts.ScriptKind.TSX,
  );
  const translatorNames = new Set([property]);
  const factory =
    property === 'copy'
      ? 'createAuthTranslator'
      : property === 'admin' || property === 'profile'
        ? 'createAdministrationTranslator'
        : 'getTranslations';
  const translationFactories = new Set([factory]);
  const collectImports = (node: ts.Node): void => {
    if (ts.isImportSpecifier(node) && (node.propertyName?.text ?? node.name.text) === factory) {
      translationFactories.add(node.name.text);
    }
    ts.forEachChild(node, collectImports);
  };
  collectImports(file);
  const unwrap = (expression: ts.Expression): ts.Expression =>
    ts.isAwaitExpression(expression) || ts.isParenthesizedExpression(expression)
      ? unwrap(expression.expression)
      : expression;
  const propertyName = (expression: ts.Expression): string | undefined =>
    ts.isPropertyAccessExpression(expression)
      ? expression.name.text
      : ts.isElementAccessExpression(expression) &&
          ts.isStringLiteralLike(expression.argumentExpression)
        ? expression.argumentExpression.text
        : undefined;
  const isTranslator = (expression: ts.Expression): boolean => {
    const value = unwrap(expression);
    return (
      (ts.isIdentifier(value) && translatorNames.has(value.text)) ||
      propertyName(value) === property ||
      (ts.isCallExpression(value) &&
        ts.isIdentifier(value.expression) &&
        translationFactories.has(value.expression.text))
    );
  };
  const collectBindings = (node: ts.Node): void => {
    if (ts.isVariableDeclaration(node) && ts.isIdentifier(node.name) && node.initializer) {
      if (isTranslator(node.initializer)) translatorNames.add(node.name.text);
    }
    if (ts.isVariableDeclaration(node) && ts.isArrayBindingPattern(node.name) && node.initializer) {
      const initializer = unwrap(node.initializer);
      if (
        ts.isCallExpression(initializer) &&
        ts.isPropertyAccessExpression(initializer.expression) &&
        initializer.expression.expression.getText(file) === 'Promise' &&
        initializer.expression.name.text === 'all' &&
        initializer.arguments[0] &&
        ts.isArrayLiteralExpression(initializer.arguments[0])
      ) {
        const values = initializer.arguments[0].elements;
        node.name.elements.forEach((binding, index) => {
          const value = values[index];
          if (
            ts.isBindingElement(binding) &&
            ts.isIdentifier(binding.name) &&
            value &&
            isTranslator(value)
          )
            translatorNames.add(binding.name.text);
        });
      }
    }
    if (ts.isVariableDeclaration(node) && ts.isObjectBindingPattern(node.name)) {
      for (const element of node.name.elements) {
        if (
          (element.propertyName?.getText(file) ?? element.name.getText(file)) === property &&
          ts.isIdentifier(element.name)
        )
          translatorNames.add(element.name.text);
      }
    }
    ts.forEachChild(node, collectBindings);
  };
  collectBindings(file);
  const keys: string[] = [];
  const visit = (node: ts.Node): void => {
    if (ts.isPropertyAccessExpression(node) || ts.isElementAccessExpression(node)) {
      const member = propertyName(node);
      if (member && ['rich', 'raw', 'has'].includes(member) && isTranslator(node.expression))
        throw new Error('Rich/raw/has UI translator methods are forbidden');
    }
    if (ts.isCallExpression(node)) {
      if (isTranslator(node.expression)) {
        const key = node.arguments[0];
        if (!key || !ts.isStringLiteralLike(key))
          throw new Error('UI callsite key must be literal');
        keys.push(key.text);
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(file);
  return keys;
}
