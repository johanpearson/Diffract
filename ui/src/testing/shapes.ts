/**
 * A small runtime description of a JSON value, for checking the contract samples against the protocol types.
 * `object<T>()` must name every field of T and no others, so a shape cannot drift from its interface without
 * failing the type check.
 */
export type Shape =
  | { kind: "string" | "number" | "boolean" | "date" | "null" }
  | { kind: "enum"; values: readonly string[] }
  | { kind: "nullable"; inner: Shape }
  | { kind: "optional"; inner: Shape }
  | { kind: "array"; item: Shape }
  | { kind: "object"; fields: Record<string, Shape> };

export const string: Shape = { kind: "string" };
export const number: Shape = { kind: "number" };
export const boolean: Shape = { kind: "boolean" };
/** An ISO 8601 date string. */
export const date: Shape = { kind: "date" };
export const nullValue: Shape = { kind: "null" };

export const oneOf = (values: readonly string[]): Shape => ({ kind: "enum", values });
export const nullable = (inner: Shape): Shape => ({ kind: "nullable", inner });
/** The field may be missing. */
export const optional = (inner: Shape): Shape => ({ kind: "optional", inner });
export const arrayOf = (item: Shape): Shape => ({ kind: "array", item });
export const object = <T>(fields: { [K in keyof T]-?: Shape }): Shape => ({ kind: "object", fields });

/** Every way `value` differs from `shape`, as "path: problem". Empty when it matches. */
export function check(shape: Shape, value: unknown, path = "$"): string[] {
  switch (shape.kind) {
    case "string":
    case "number":
    case "boolean":
      return typeof value === shape.kind ? [] : [`${path}: expected a ${shape.kind}, got ${JSON.stringify(value)}`];
    case "date":
      return typeof value === "string" && /^\d{4}-\d\d-\d\dT/.test(value) && !Number.isNaN(Date.parse(value))
        ? []
        : [`${path}: expected an ISO date, got ${JSON.stringify(value)}`];
    case "null":
      return value === null ? [] : [`${path}: expected null, got ${JSON.stringify(value)}`];
    case "enum":
      return typeof value === "string" && shape.values.includes(value)
        ? []
        : [`${path}: expected one of ${shape.values.join(", ")}, got ${JSON.stringify(value)}`];
    case "nullable":
      return value === null ? [] : check(shape.inner, value, path);
    case "optional":
      return value === undefined ? [] : check(shape.inner, value, path);
    case "array":
      return Array.isArray(value)
        ? value.flatMap((item, i) => check(shape.item, item, `${path}[${i}]`))
        : [`${path}: expected an array, got ${JSON.stringify(value)}`];
    case "object": {
      if (typeof value !== "object" || value === null || Array.isArray(value)) return [`${path}: expected an object, got ${JSON.stringify(value)}`];
      const record = value as Record<string, unknown>;
      const missing = Object.entries(shape.fields)
        .filter(([key, field]) => !(key in record) && field.kind !== "optional")
        .map(([key]) => `${path}.${key}: missing`);
      const unexpected = Object.keys(record)
        .filter((key) => !(key in shape.fields))
        .map((key) => `${path}.${key}: not in the TypeScript type`);
      const nested = Object.entries(shape.fields)
        .filter(([key]) => key in record)
        .flatMap(([key, field]) => check(field, record[key], `${path}.${key}`));
      return [...missing, ...unexpected, ...nested];
    }
  }
}
