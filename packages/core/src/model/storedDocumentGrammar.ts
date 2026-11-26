/** The rules a reader's fault carries, one member each, and the only ids a fault names. A port type field faults
 * as `L7`, and an embedded port list under the rule its own text breaks. */
export const STORED_DOCUMENT_RULES = Object.freeze({ L1: 'L1', L2: 'L2', L3: 'L3', L4: 'L4', L5: 'L5', L7: 'L7', DuplicateNodeId: 'duplicate-node-id', DuplicateEdgeId: 'duplicate-edge-id', OutputMappingsUnread: 'output-mappings-unread', UnknownKey: 'stored-key-unknown', DottedName: 'dotted-name', ErrorPolicyRouteUnread: 'error-policy-route-unread' } as const)

/** The rule broken: L1–L5 and L7 describe text and shape faults; named rules cover duplicate ids and dotted names. */
export type StoredDocumentRule = (typeof STORED_DOCUMENT_RULES)[keyof typeof STORED_DOCUMENT_RULES]

/** Every rule id of the reader, for the test that holds the contract to them. */
export const STORED_DOCUMENT_RULE_IDS: readonly StoredDocumentRule[] = Object.values(STORED_DOCUMENT_RULES)

export type Role = 'none' | 'canvas' | 'node' | 'connection' | 'declaredOutput' | 'portEntry' | 'parameter' | 'typeArguments' | 'projection'

export type Kind =
  | { of: 'string'; nonEmpty: boolean }
  | { of: 'number' }
  | { of: 'boolean' }
  | { of: 'refused'; rule: StoredDocumentRule; detail: string }
  | { of: 'any' }
  | { of: 'enum'; names: readonly string[]; noun?: string }
  | { of: 'portType' }
  | ArrayKind
  | MapKind
  | { of: 'config' }
  | ObjectKind

export interface ArrayKind { of: 'array'; element: Kind }
export interface MapKind { of: 'map'; value: Kind; role?: 'subCanvases' | 'typeArguments' }
export interface Field { key: string; kind: Kind; nullable: boolean; required: boolean; refusedRule?: StoredDocumentRule; refusedDetail?: string }
export interface ObjectKind { of: 'object'; name: string; fields: Field[]; role: Role }

const text = (nonEmpty = false): Kind => ({ of: 'string', nonEmpty })
const field = (key: string, kind: Kind, options: { nullable?: boolean; required?: boolean; refusedRule?: StoredDocumentRule; refusedDetail?: string } = {}): Field =>
  ({ key, kind, nullable: options.nullable ?? false, required: options.required ?? false, ...(options.refusedRule ? { refusedRule: options.refusedRule } : {}), ...(options.refusedDetail ? { refusedDetail: options.refusedDetail } : {}) })
const object = (name: string, fields: Field[], role: Role = 'none'): ObjectKind =>
  ({ of: 'object', name, fields, role })
const arrayOf = (element: Kind): ArrayKind => ({ of: 'array', element })
const mapOf = (value: Kind): MapKind => ({ of: 'map', value })

const PORT_TYPE: Kind = { of: 'portType' }

export const PARAMETER_LIST = arrayOf(object('parameter', [
  field('name', text(true), { required: true }),
  field('type', PORT_TYPE, { nullable: true }),
  field('isRequired', { of: 'boolean' }),
  field('displayName', text()),
], 'parameter'))

export const PORT_LIST = arrayOf(object('port entry', [
  field('name', text(true), { required: true }),
  field('type', PORT_TYPE, { nullable: true }),
  field('isRequired', { of: 'boolean' }),
  field('displayName', text()),
], 'portEntry'))

/** What a direct mapping selects from its source: the one encoding of a projection's shape. */
export const PROJECTION: ObjectKind = object('projection', [
  field('path', text(), { required: true }),
  field('mode', { of: 'enum', names: ['jsonText'], noun: 'a projection mode' }),
  field('type', PORT_TYPE),
], 'projection')

export const DEFINITION: ObjectKind = (() => {
  const projection = PROJECTION
  const connection = object('connection', [
    field('id', text(true), { required: true }),
    field('sourceNodeId', text(true), { required: true }),
    field('targetNodeId', text(true), { required: true }),
    field('mappings', mapOf(object('mapping', [
      field('type', { of: 'enum', names: ['direct', 'literal'], noun: 'a mapping type' }, { required: true }),
      field('sourceOutputName', text(true)),
      field('literalValue', { of: 'any' }),
      field('projection', projection),
    ])), { required: true }),
  ], 'connection')
  const outputMapping = object('output mapping', [field('sourceNodeId', text()), field('sourceOutput', text()), field('workflowOutput', text())])
  const declaredOutput = object('declared output', [
    field('name', text(true), { required: true }),
    field('displayName', text()),
    field('type', PORT_TYPE, { nullable: true }),
    field('sources', mapOf(text())),
  ], 'declaredOutput')
  const errorPolicy = object('error policy', [
    field('policy', { of: 'enum', names: ['FailWorkflow', 'ContinueWithDefault', 'EmitError', 'Skip'] }, { required: true }),
    field('defaultOutputJson', text()),
    field('routeToErrorPort', { of: 'refused', rule: 'error-policy-route-unread', detail: 'a routeToErrorPort key no run reads' }),
  ])
  const position = object('position', [field('x', { of: 'number' }, { required: true }), field('y', { of: 'number' }, { required: true })])
  const canvas = object('canvas', [], 'canvas')
  const node = object('node', [
    field('id', text(true), { required: true }),
    field('blockTypeId', text()),
    field('position', position, { required: true }),
    field('configuration', { of: 'config' }, { required: true }),
    field('subCanvases', { ...mapOf(canvas), role: 'subCanvases' }),
    field('passthrough', arrayOf(text(true))),
    field('outputPorts', arrayOf(declaredOutput)),
    field('errorPolicy', errorPolicy),
    field('typeArguments', { ...mapOf(PORT_TYPE), role: 'typeArguments' }),
  ], 'node')
  canvas.fields.push(
    field('nodes', arrayOf(node), { required: true }),
    field('edges', arrayOf(connection), { required: true }),
    field('outputMappings', arrayOf(outputMapping), { nullable: true, refusedRule: STORED_DOCUMENT_RULES.OutputMappingsUnread, refusedDetail: 'outputMappings is not stored; an output is a wire into the WorkflowOutput node' }),
  )
  return canvas
})()

/** One path of the kind table and the value classes it admits: what `checkGenerated` holds the contract to. */
export interface GrammarPath {
  readonly path: string
  readonly values: readonly string[]
}

const JSON_KINDS: readonly string[] = ['boolean', 'number', 'string', 'array', 'object']

const CONFIGURATION_VALUES: readonly string[] = [
  ...JSON_KINDS.map((kind) => `value-${kind}`),
  'value-null',
  'value-absent',
  'value-blank-port-list',
  'value-port-list-without-dynamic-ports',
]

function valueClasses(kind: Kind): string[] {
  switch (kind.of) {
    case 'string': return ['string']
    case 'number': return ['number']
    case 'boolean': return ['boolean']
    case 'refused': return ['boolean']
    case 'any': return [...JSON_KINDS]
    case 'config': return [...CONFIGURATION_VALUES]
    case 'enum': return [...kind.names]
    case 'portType': return ['atom', 'collection', 'nested-collection', 'alias', 'unregistered', 'collection-of-any']
    case 'array': return ['array']
    case 'map': case 'object': return ['object']
  }
}

function visit(
  kind: Kind,
  onObject: (kind: ObjectKind) => void,
  onField: (kind: ObjectKind, entry: Field) => void,
  seen: Set<ObjectKind>,
): void {
  switch (kind.of) {
    case 'object':
      if (seen.has(kind)) return
      seen.add(kind)
      onObject(kind)
      for (const entry of kind.fields) {
        onField(kind, entry)
        visit(entry.kind, onObject, onField, seen)
      }
      return
    case 'array': return visit(kind.element, onObject, onField, seen)
    case 'map': return visit(kind.value, onObject, onField, seen)
    default: return
  }
}

/** Every path of the kind table, in the order the backend's `Describe` visits them: the definition's, then a
 * parameter's, then a port list entry's. */
export function grammarPaths(): GrammarPath[] {
  const paths: GrammarPath[] = []
  const seen = new Set<ObjectKind>()
  const collect = (kind: ObjectKind, entry: Field): void => {
    paths.push({ path: `${kind.name}.${entry.key}`, values: valuesOf(entry) })
  }
  const none = (): void => undefined
  visit(DEFINITION, none, collect, seen)
  visit(PARAMETER_LIST, none, collect, seen)
  visit(PORT_LIST, none, collect, seen)
  return paths
}

/** Every object kind of the kind table, each with the one row it holds: the unlisted key the reader refuses.
 * `canvas` is met first, its sub-canvas being itself. */
export function grammarObjects(): GrammarPath[] {
  const objects: GrammarPath[] = []
  const seen = new Set<ObjectKind>()
  const collect = (kind: ObjectKind): void => {
    objects.push({ path: kind.name, values: ['unlisted-key'] })
  }
  const none = (): void => undefined
  visit(DEFINITION, collect, none, seen)
  visit(PARAMETER_LIST, collect, none, seen)
  visit(PORT_LIST, collect, none, seen)
  return objects
}

/** The value classes of a field: its kind's, then `null` where it may hold one, then `absent` where it is optional.
 * A configuration field's classes open with `object`, the field's own shape, and not a value's. */
export function valuesOf(entry: Field): string[] {
  const values = entry.kind.of === 'config' ? ['object', ...valueClasses(entry.kind)] : valueClasses(entry.kind)
  if (entry.nullable) values.push('null')
  if (!entry.required) values.push('absent')
  return values
}
