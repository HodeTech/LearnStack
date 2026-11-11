import { fields, object, text } from './administration-policy.js';
const whole = (value: unknown, min: number, max: number): value is number =>
  typeof value === 'number' && Number.isSafeInteger(value) && value >= min && value <= max;
const numeric = (value: unknown): value is number =>
  typeof value === 'number' &&
  Number.isFinite(value) &&
  (!Number.isInteger(value) || Number.isSafeInteger(value));
const key = (value: string) =>
  value.length <= 100 &&
  /^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(value) &&
  !['constructor', 'prototype', '__proto__'].includes(value);
/** Validate the closed form grammar at the untrusted SDK response boundary. */
export function customFieldSchema(value: unknown): boolean {
  try {
    if (new TextEncoder().encode(JSON.stringify(value)).length > 2048) return false;
  } catch {
    return false;
  }
  if (
    !fields(value, ['$schema', 'type', 'properties', 'required', 'additionalProperties']) ||
    value.$schema !== 'https://json-schema.org/draft/2020-12/schema' ||
    value.type !== 'object' ||
    value.additionalProperties !== false ||
    !fields(value.properties, ['value']) ||
    !Array.isArray(value.required) ||
    value.required.length !== 1 ||
    value.required[0] !== 'value'
  )
    return false;
  const node = (shape: unknown, depth: number): boolean => {
    if (!object(shape)) return false;
    switch (shape.type) {
      case 'string':
        return (
          fields(
            shape,
            shape.enum === undefined
              ? ['type', 'minLength', 'maxLength']
              : ['type', 'minLength', 'maxLength', 'enum'],
          ) &&
          whole(shape.minLength, 1, 256) &&
          whole(shape.maxLength, shape.minLength, 256) &&
          (shape.enum === undefined ||
            (Array.isArray(shape.enum) &&
              shape.enum.length >= 1 &&
              shape.enum.length <= 20 &&
              new Set(shape.enum).size === shape.enum.length &&
              shape.enum.every(
                (item) =>
                  text(item, 512, true, true) &&
                  [...item].length >= (shape.minLength as number) &&
                  [...item].length <= (shape.maxLength as number),
              )))
        );
      case 'integer':
      case 'number':
        return (
          fields(shape, ['type', 'minimum', 'maximum']) &&
          numeric(shape.minimum) &&
          numeric(shape.maximum) &&
          shape.minimum <= shape.maximum &&
          (shape.type !== 'integer' ||
            (Number.isSafeInteger(shape.minimum) && Number.isSafeInteger(shape.maximum)))
        );
      case 'boolean':
        return fields(shape, ['type']);
      case 'array':
        return (
          depth < 2 &&
          fields(shape, ['type', 'items', 'minItems', 'maxItems']) &&
          whole(shape.minItems, 0, 10) &&
          whole(shape.maxItems, Math.max(1, shape.minItems), 10) &&
          node(shape.items, depth + 1)
        );
      case 'object': {
        if (
          depth >= 2 ||
          !fields(shape, ['type', 'properties', 'required', 'additionalProperties']) ||
          !object(shape.properties) ||
          !Array.isArray(shape.required) ||
          shape.additionalProperties !== false
        )
          return false;
        const names = Object.keys(shape.properties);
        return (
          names.length >= 1 &&
          names.length <= 10 &&
          names.every(key) &&
          shape.required.every((item) => typeof item === 'string' && names.includes(item)) &&
          new Set(shape.required).size === shape.required.length &&
          Object.values(shape.properties).every((child) => node(child, depth + 1))
        );
      }
      default:
        return false;
    }
  };
  return node(value.properties.value, 0);
}
