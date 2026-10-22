/**
 * A small runtime description of a JSON value, for checking the contract samples against the protocol types.
 * `object<T>()` must name every field of T and no others, so a shape cannot drift from its interface without
 * failing the type check.
 */
export type Shape = PlainShape | EnumShape | NullableShape | OptionalShape | ArrayShape | ObjectShape;

type PlainShape = { kind: "string" | "number" | "boolean" | "date" | "null" };
type EnumShape = { kind: "enum"; values: readonly string[] };
type NullableShape = { kind: "nullable"; inner: Shape };
type OptionalShape = { kind: "optional"; inner: Shape };
type ArrayShape = { kind: "array"; item: Shape };
type ObjectShape = { kind: "object"; fields: Record<string, Shape> };

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
  return checkers[shape.kind](shape, value, path);
}

type Checker = (shape: Shape, value: unknown, path: string) => string[];

const checkers: Record<Shape["kind"], Checker> = {
  string: checkTypeOf,
  number: checkTypeOf,
  boolean: checkTypeOf,
  date: (_, value, path) => (isIsoDate(value) ? [] : [`${path}: expected an ISO date, got ${JSON.stringify(value)}`]),
  null: (_, value, path) => (value === null ? [] : [`${path}: expected null, got ${JSON.stringify(value)}`]),
  enum: (shape, value, path) => checkEnum(shape as EnumShape, value, path),
  nullable: (shape, value, path) => (value === null ? [] : check((shape as NullableShape).inner, value, path)),
  optional: (shape, value, path) => (value === undefined ? [] : check((shape as OptionalShape).inner, value, path)),
  array: (shape, value, path) => checkArray(shape as ArrayShape, value, path),
  object: (shape, value, path) => checkObject(shape as ObjectShape, value, path),
};

function checkTypeOf(shape: Shape, value: unknown, path: string): string[] {
  return typeof value === shape.kind ? [] : [`${path}: expected a ${shape.kind}, got ${JSON.stringify(value)}`];
}

function isIsoDate(value: unknown): boolean {
  return typeof value === "string" && /^\d{4}-\d\d-\d\dT/.test(value) && !Number.isNaN(Date.parse(value));
}

function checkEnum(shape: EnumShape, value: unknown, path: string): string[] {
  return typeof value === "string" && shape.values.includes(value)
    ? []
    : [`${path}: expected one of ${shape.values.join(", ")}, got ${JSON.stringify(value)}`];
}

function checkArray(shape: ArrayShape, value: unknown, path: string): string[] {
  return Array.isArray(value)
    ? value.flatMap((item, i) => check(shape.item, item, `${path}[${i}]`))
    : [`${path}: expected an array, got ${JSON.stringify(value)}`];
}

function checkObject(shape: ObjectShape, value: unknown, path: string): string[] {
  if (!isRecord(value)) return [`${path}: expected an object, got ${JSON.stringify(value)}`];
  const missing = Object.entries(shape.fields)
    .filter(([key, field]) => !(key in value) && field.kind !== "optional")
    .map(([key]) => `${path}.${key}: missing`);
  const unexpected = Object.keys(value)
    .filter((key) => !(key in shape.fields))
    .map((key) => `${path}.${key}: not in the TypeScript type`);
  const nested = Object.entries(shape.fields)
    .filter(([key]) => key in value)
    .flatMap(([key, field]) => check(field, value[key], `${path}.${key}`));
  return [...missing, ...unexpected, ...nested];
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
