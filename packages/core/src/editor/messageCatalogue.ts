import type { ValidationFinding } from '../model/workflow.js'

/** Host translations by finding code. A function receives the finding's params and the finding itself. */
export type ValidationMessageCatalogue = Readonly<Record<string, (params: Readonly<Record<string, unknown>>, finding: ValidationFinding) => string>>

/** One library code: the parameters its English template reads, and the template. */
export interface ValidationCatalogueEntry {
  readonly code: string
  /** Every one must be present on a finding for `render` to be used; otherwise the finding's own `message` shows. */
  readonly requiredParams: readonly string[]
  readonly render: (params: Readonly<Record<string, unknown>>) => string
}

const str = (p: Readonly<Record<string, unknown>>, key: string): string => String(p[key])
const entry = (
  code: string,
  requiredParams: readonly string[],
  render: (p: Readonly<Record<string, unknown>>) => string,
): ValidationCatalogueEntry => ({ code, requiredParams, render })

const ENTRIES: readonly ValidationCatalogueEntry[] = [
  entry('cycle', ['location'], (p) => `Workflow contains a cycle${str(p, 'location')}`),
  entry('block-port-declaration-invalid', ['blockTypeId', 'where', 'reason'], (p) =>
    `Block '${str(p, 'blockTypeId')}' ${str(p, 'where')} ${str(p, 'reason')}.`),
  entry('no-start-node', [], () => 'Workflow has no start node. Add a Workflow Input node as the single entry point.'),
  entry('input-has-incoming', [], () => 'The Workflow Input node must be the single start point and cannot have incoming connections'),
  entry('extra-root', ['nodeLabel'], (p) => `Node '${str(p, 'nodeLabel')}' has no incoming connection — the workflow must start only from the Workflow Input node`),
  entry('unreachable', ['nodeLabel'], (p) => `Node '${str(p, 'nodeLabel')}' is not reachable from the Workflow Input start node`),
  entry('duplicate-input-mapping', ['nodeLabel', 'inputName', 'location'], (p) =>
    `Node '${str(p, 'nodeLabel')}' input '${str(p, 'inputName')}' is mapped from multiple incoming connections${str(p, 'location')} — only one will apply`),
  entry('duplicate-edge-pair', ['location', 'sourceNodeId', 'targetNodeId'], (p) =>
    `More than one connection joins '${str(p, 'sourceNodeId')}' to '${str(p, 'targetNodeId')}'${str(p, 'location')}`),
  entry('duplicate-edge-id', ['fault'], (p) => `Connection id is duplicated: ${str(p, 'fault')}.`),
  entry('edge-endpoint-unknown', ['edgeId', 'location', 'end', 'nodeId'], (p) =>
    `Connection '${str(p, 'edgeId')}'${str(p, 'location')} names ${str(p, 'end')} node '${str(p, 'nodeId')}', which does not exist on its canvas`),
  entry('duplicate-node-id', ['duplicateId', 'location'], (p) => `Node id '${str(p, 'duplicateId')}' is used by more than one node${str(p, 'location')}`),
  entry('required-input-unfed', ['nodeLabel', 'portLabel'], (p) => `Node '${str(p, 'nodeLabel')}' is missing required mapping for '${str(p, 'portLabel')}'`),
  entry('required-input-unfed.exit', ['exitNoun', 'portLabel'], (p) => `${str(p, 'exitNoun')} '${str(p, 'portLabel')}' is not connected`),
  entry('reference-input-mapped', ['nodeLabel', 'reference'], (p) =>
    `Node '${str(p, 'nodeLabel')}' takes the workflow it runs from its '${str(p, 'reference')}' setting only; a mapping into it is refused`),
  entry('node-ports-unresolved', ['nodeLabel', 'reference'], (p) => `Node '${str(p, 'nodeLabel')}' names no workflow to run in '${str(p, 'reference')}'`),
  entry('sub-canvas-name-unknown', ['nodeLabel', 'branchName'], (p) => `Node '${str(p, 'nodeLabel')}' has a branch '${str(p, 'branchName')}' its block does not declare`),
  entry('required-config-missing', ['nodeLabel', 'fieldLabel'], (p) => `Node '${str(p, 'nodeLabel')}' is missing required configuration field '${str(p, 'fieldLabel')}'`),
  entry('resilience-config-invalid', ['nodeLabel', 'key', 'value', 'expectation'], (p) =>
    `Node '${str(p, 'nodeLabel')}' has '${str(p, 'key')}' set to ${str(p, 'value')}, which the engine cannot read as ${str(p, 'expectation')}`),
  entry('resilience-config-invalid.timeout-too-long', ['nodeLabel', 'key', 'value', 'maxSeconds'], (p) =>
    `Node '${str(p, 'nodeLabel')}' sets '${str(p, 'key')}' to ${str(p, 'value')}, and the longest deadline the engine applies is ${str(p, 'maxSeconds')} seconds (one day)`),
  entry('declared-output-shadows-static', ['nodeLabel', 'portName'], (p) =>
    `Node '${str(p, 'nodeLabel')}' declared output '${str(p, 'portName')}' shadows a static output — rename it to avoid collision`),
  entry('error-policy-in-configuration', ['nodeLabel'], (p) =>
    `Node '${str(p, 'nodeLabel')}' keeps its error policy in its configuration, where the run never reads it: set the policy on the node instead`),
  entry('error-policy-in-configuration.boundary', ['nodeLabel'], (p) =>
    `Node '${str(p, 'nodeLabel')}' keeps an error policy in its configuration, where the run never reads it, and an entry or exit node has none: remove the key`),
  entry('error-policy-route-unread', ['nodeLabel'], (p) =>
    `Node '${str(p, 'nodeLabel')}' sets 'routeToErrorPort', which no run reads: remove it. A node offers '_error' under EmitError alone`),
  entry('wire-port-unknown.output', ['nodeLabel', 'portName', 'edgeId'], (p) =>
    `Node '${str(p, 'nodeLabel')}' has no output '${str(p, 'portName')}' for connection '${str(p, 'edgeId')}' to map`),
  entry('wire-port-unknown.input', ['nodeLabel', 'portName', 'edgeId'], (p) =>
    `Node '${str(p, 'nodeLabel')}' has no input '${str(p, 'portName')}' for connection '${str(p, 'edgeId')}' to feed`),
  entry('port-type-malformed', ['portName', 'detail', 'origin'], (p) =>
    `Port type of '${str(p, 'portName')}' is not a well-formed name: ${str(p, 'detail')} It is ${str(p, 'origin')}.`),
  entry('port-type-map-key', ['portName', 'detail', 'origin'], (p) =>
    `Port type of '${str(p, 'portName')}' has a Map type whose key is not String: ${str(p, 'detail')} It is ${str(p, 'origin')}.`),
  entry('port-type-json-position', ['portName'], (p) => `Port type '${str(p, 'portName')}' contains Json, which is not allowed here.`),
  entry('type-argument-unknown', ['name', 'blockType'], (p) => `Type argument '${str(p, 'name')}' names no type parameter of block '${str(p, 'blockType')}'.`),
  entry('literal-port-value-invalid', ['nodeLabel', 'portName', 'expectedType'], (p) =>
    `Node '${str(p, 'nodeLabel')}' has a value for '${str(p, 'portName')}' that cannot be read as ${str(p, 'expectedType')}.`),
  entry('literal-port-value-invalid.enum', ['nodeLabel', 'portName', 'allowedValues'], (p) =>
    `Node '${str(p, 'nodeLabel')}' must set '${str(p, 'portName')}' to one of ${str(p, 'allowedValues')}.`),
  entry('signature-unreadable', ['subject', 'fault'], (p) => `${str(p, 'subject')} cannot be read: ${str(p, 'fault')}.`),
  entry('stored-key-unknown', ['subject', 'fault'], (p) => `${str(p, 'subject')} cannot be read: ${str(p, 'fault')}.`),
  entry('output-mappings-unread', ['subject', 'fault'], (p) => `${str(p, 'subject')} cannot be read: ${str(p, 'fault')}.`),
  entry('definition-unreadable', ['subject', 'fault'], (p) => `${str(p, 'subject')} cannot be read: ${str(p, 'fault')}.`),
  entry('definition-undrawable', ['fault'], (p) => `The editor cannot draw this workflow: ${str(p, 'fault')}.`),
  entry('branch-output-unset', ['branchName', 'nodeLabel', 'portName'], (p) =>
    `Branch '${str(p, 'branchName')}' of '${str(p, 'nodeLabel')}' does not set output '${str(p, 'portName')}' (returns null)`),
  entry('branch-output-maybe-unset', ['nodeLabel', 'portName', 'satisfyingBranches'], (p) =>
    `Output '${str(p, 'portName')}' of '${str(p, 'nodeLabel')}' may be null on some execution path (set it ${str(p, 'satisfyingBranches')})`),
  entry('validation-rule-failed', ['nodeLabel', 'reason'], (p) => `A validation rule failed on '${str(p, 'nodeLabel')}': ${str(p, 'reason')}`),
  entry('host-metadata-invalid', ['detail'], (p) => `The workflow's host metadata cannot be stored. ${str(p, 'detail')}`),
  entry('secret-value-unmatched', ['detail'], (p) => `A hidden or protected value cannot be kept. ${str(p, 'detail')}`),
  entry('secret-codec-missing', ['detail'], (p) => `A value cannot be hidden because this host stores no protected values. ${str(p, 'detail')}`),
  entry('secret-refused', ['detail'], (p) => `The host did not accept a secret in this workflow. ${str(p, 'detail')}`),
  entry('secret-key-invalid', ['detail'], (p) => `A header name is not valid. ${str(p, 'detail')}`),
  entry('origin-not-accepted', ['detail'], (p) => `This request cannot declare that origin. ${str(p, 'detail')}`),
  entry('secret-unresolved', ['input', 'entry'], (p) => `Input '${str(p, 'input')}' ${str(p, 'entry')} holds a secret that could not be revealed.`),
]

/**
 * Compiler rule ids the library holds no English template for; a publish diagnostic for one shows the
 * server's message.
 */
export const MESSAGE_ONLY_VALIDATION_CODES: readonly string[] = [
  'structure', 'block-type-unknown', 'wire-port-unknown', 'port-type-mismatch', 'compilation-fault', 'port-type-unresolved',
  'expression-syntax', 'expression-out-of-scope', 'expression-unknown-namespace', 'node-id-too-long', 'block-type-id-too-long',
  'dotted-name', 'port-type-alias', 'port-type-unregistered', 'expression-field-access-invalid', 'expression-type-mismatch',
  'expression-result-type-mismatch', 'error-policy-on-boundary-node',
  'port-list-without-dynamic-ports', 'declared-outputs-unsupported', 'declared-output-source-unknown',
  'type-parameter-conflict', 'type-parameter-unbound', 'type-parameter-constraint', 'json-projection-invalid',
  'secret-input-passthrough', 'secret-input-key-invalid',
]

/** The English templates, by code. A finding is rendered from its entry only when it carries every `requiredParams`. */
export const ENGLISH_VALIDATION_MESSAGES: Readonly<Record<string, ValidationCatalogueEntry>> = Object.freeze(
  Object.fromEntries(ENTRIES.map((e) => [e.code, e])),
)

/** Every code the library raises or accepts from the compiler: a template entry or a message-only code. */
export const LIBRARY_VALIDATION_CODES: readonly string[] = [...ENTRIES.map((e) => e.code), ...MESSAGE_ONLY_VALIDATION_CODES]

const has = (record: object, key: string): boolean => Object.hasOwn(record, key)

/**
 * The one renderer. A library code renders by its template when the finding carries every parameter the template
 * reads (a host's override of that code replaces the template under the same condition); a finding short of one,
 * a message-only code, or a code nobody has an entry for shows its own `message`. A host code's override is
 * used whenever it has one.
 */
export function validationMessage(finding: ValidationFinding, catalogue?: ValidationMessageCatalogue): string {
  const params = finding.params ?? {}
  const english = has(ENGLISH_VALIDATION_MESSAGES, finding.code) ? ENGLISH_VALIDATION_MESSAGES[finding.code] : undefined
  const override = catalogue && has(catalogue, finding.code) ? catalogue[finding.code] : undefined
  if (english === undefined) return override ? override(params, finding) : finding.message
  if (!english.requiredParams.every((key) => params[key] !== undefined && params[key] !== null)) return finding.message
  return override ? override(params, finding) : english.render(params)
}
