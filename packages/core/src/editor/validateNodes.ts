import type { BlockPort, BlockTypeDescriptor } from '../model/blockType.js'
import type { ValidationFinding, WorkflowEdge, WorkflowNode, WorkflowParameter } from '../model/workflow.js'
import { ERROR_OUTPUT_NAME, offersErrorOutput, readMapping, WORKFLOW_INPUT_BLOCK_TYPE_ID, WORKFLOW_OUTPUT_BLOCK_TYPE_ID } from '../model/workflow.js'
import { INPUT_PORTS_KEY, OUTPUT_PORTS_KEY } from '../model/storedDocument.js'
import { isGradableTerm } from '../portTypes/closedTerm.js'
import type { PortTypeName } from '../portTypes/name.js'
import type { PortTypeRegistry } from '../portTypes/types.js'
import { parseDynamicPorts } from '../ports/dynamicPorts.js'
import { boundBlockTypes, canvasSorts } from './canvasFrames.js'
import { documentNodeLabels } from './nodeLabels.js'
import { feedsInput, isActiveMapping } from './mappingFeeds.js'
import type { TraceCanvas } from './passthroughTrace.js'
import { hasMalformedStoredPortType, tracedPassthrough } from './passthroughTrace.js'
import type { NodeValidationRule } from './validationRules.js'
import { runNodeRules } from './validationRules.js'

/** A port an exit node takes: a workflow output parameter, or a branch output a container declares. */
export type ExitPort = Pick<WorkflowParameter, 'name' | 'displayName' | 'isRequired' | 'type'>

/** The names a wire may map out of this canvas's entry nodes and into its exit nodes, as the compiler's signature has them. */
export interface CanvasPortNames {
  entry: readonly string[]
  exit: readonly string[]
}

const RESILIENCE: readonly (readonly [string, 'whole' | 'number' | 'boolean'])[] = [
  ['timeoutSeconds', 'whole'], ['retryCount', 'whole'], ['retryEnabled', 'boolean'], ['retryBackoffSeconds', 'number'],
]
const LONGEST_ENGINE_WAIT_SECONDS = 86400
const DESIGN_TIME_PREFIX = '__'
const READS_AS = { whole: 'a whole number', number: 'a number', boolean: 'true or false' } as const

const own = (config: WorkflowNode['configuration'], key: string): unknown =>
  Object.hasOwn(config, key) ? config[key] : undefined
const isFilled = (value: unknown): boolean => value !== undefined && value !== null && value !== ''
const isConfigured = (value: unknown): boolean =>
  value !== undefined && value !== null && !(typeof value === 'number' && !Number.isFinite(value))
const isExpressionText = (value: unknown): boolean => typeof value === 'string' && value.startsWith('expr:')

const isWhole = (value: unknown): value is number =>
  typeof value === 'number' && Number.isInteger(value) && value >= -2147483648 && value <= 2147483647
const readsAs = (value: unknown, kind: 'whole' | 'number' | 'boolean'): boolean =>
  kind === 'whole' ? isWhole(value) : kind === 'number' ? typeof value === 'number' : typeof value === 'boolean'

const mergesConfigKey = (node: WorkflowNode, portName: string): boolean =>
  !RESILIENCE.some(([key]) => key === portName) && !portName.startsWith(DESIGN_TIME_PREFIX) && own(node.configuration, portName) !== undefined
function isFedByConfigValue(node: WorkflowNode, portName: string): boolean {
  return mergesConfigKey(node, portName) && isFilled(own(node.configuration, portName))
}

/** Whether a node is a canvas's entry (`WorkflowInput`) or exit (`WorkflowOutput`) node. */
export const isBoundary = (node: WorkflowNode) =>
  node.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID || node.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID

function enumLiteralFinding(node: WorkflowNode, label: string, name: string, names: readonly string[], value: unknown): ValidationFinding | undefined {
  if (typeof value === 'string' && names.includes(value)) return undefined
  return {
    code: 'literal-port-value-invalid.enum', ruleId: 'literal-port-value-invalid', nodeId: node.id, portName: name,
    params: { nodeLabel: label, portName: name, allowedValues: names.map((n) => `'${n}'`).join(', ') },
    message: `Node '${label}' must set '${name}' to one of ${names.map((n) => `'${n}'`).join(', ')}.`,
  }
}

function dynamicPorts(node: WorkflowNode, blockType: BlockTypeDescriptor | undefined, key: string) {
  const text = own(node.configuration, key)
  return blockType?.hasDynamicPorts && typeof text === 'string' ? parseDynamicPorts(text) : []
}

function configSiteTerms(node: WorkflowNode, blockType: BlockTypeDescriptor, portTypes: PortTypeRegistry): Map<string, PortTypeName | undefined> {
  const terms = new Map<string, PortTypeName | undefined>()
  for (const port of [...(blockType.inputs ?? []), ...dynamicPorts(node, blockType, INPUT_PORTS_KEY)]) {
    if (!terms.has(port.name)) terms.set(port.name, isGradableTerm(port.type, portTypes) ? port.type : undefined)
  }
  for (const name of node.passthrough ?? []) if (!terms.has(name)) terms.set(name, undefined)
  return new Map([...terms].filter(([name]) => mergesConfigKey(node, name)))
}

function portNames(node: WorkflowNode, blockType: BlockTypeDescriptor | undefined, side: 'inputs' | 'outputs', canvasPorts: CanvasPortNames, trace: TraceCanvas): Set<string> {
  const names = tracedPassthrough(trace, node)
  if (side === 'outputs') {
    for (const p of node.outputPorts ?? []) names.add(p.name)
    if (offersErrorOutput(node)) names.add(ERROR_OUTPUT_NAME)
    if (node.blockTypeId === WORKFLOW_INPUT_BLOCK_TYPE_ID) for (const n of canvasPorts.entry) names.add(n)
  } else if (node.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID) {
    for (const n of canvasPorts.exit) names.add(n)
  }
  if (isBoundary(node)) return names
  for (const p of blockType?.[side] ?? []) names.add(p.name)
  for (const p of dynamicPorts(node, blockType, side === 'inputs' ? INPUT_PORTS_KEY : OUTPUT_PORTS_KEY)) names.add(p.name)
  return names
}

/** Per-node errors that block a save, as the compiler grades a node on this canvas: its required inputs, configuration,
 * declared outputs, reference input, resilience keys and branch names, the wires' port names, and the exit nodes. */
export function validateNodes(input: {
  nodes: WorkflowNode[]
  edges: WorkflowEdge[]
  blockTypes: BlockTypeDescriptor[]
  /** The ports this canvas's exit nodes take: the workflow's outputs on the root, the container's branch outputs below. */
  workflowOutputParams: readonly ExitPort[]
  portTypes: PortTypeRegistry
  /** The closed term the editor knows an output to have, or `undefined`; an unknown type is never graded against. */
  resolveOutputType: (nodeId: string, portName: string) => PortTypeName | undefined
  rules?: readonly NodeValidationRule[]
  /** False where the compiler holds no ports for this canvas: below a container it lacks or a branch it does not declare. */
  signatureKnown?: boolean
  /** When given, each wire's port names are checked against them and the nodes' own ports. */
  canvasPorts?: CanvasPortNames
  /** How a message names an exit port: 'Workflow output' on the root. */
  exitNoun?: string
  /** How a message names a node: pass `documentNodeLabels(definition, blockTypes)` over the whole document, so two nodes
   * called alike are told apart. Without it a node is named by its block's display name. */
  nodeLabel?: (node: WorkflowNode) => string
}): ValidationFinding[] {
  const { nodes, edges, blockTypes, workflowOutputParams, portTypes, resolveOutputType, rules = [] } = input
  const { signatureKnown = true, canvasPorts, exitNoun = 'Workflow output' } = input
  const nodeLabel = input.nodeLabel ?? documentNodeLabels({ nodes: [] }, blockTypes)
  const errors: ValidationFinding[] = []
  const nodeIds = new Set(nodes.map((n) => n.id))
  const bound = boundBlockTypes(nodes, blockTypes)
  const portsKnown = signatureKnown && canvasSorts({ nodes, edges })
  const isFed = (node: WorkflowNode, port: string) =>
    edges.some((e) => e.targetNodeId === node.id && feedsInput(e, port, nodeIds)) || isFedByConfigValue(node, port)

  for (const node of nodes) {
    if (isBoundary(node) || bound.has(node.id)) errors.push(...errorPolicyFindings(node, nodeLabel(node)))
    if (isBoundary(node)) {
      errors.push(...undeclaredPortShapeFindings(node, NO_DECLARATIONS, node.blockTypeId))
      if (node.blockTypeId !== WORKFLOW_OUTPUT_BLOCK_TYPE_ID || !portsKnown) continue
      for (const port of workflowOutputParams.filter((p) => p.isRequired && !isFed(node, p.name))) {
        errors.push({ code: 'required-input-unfed.exit', params: { exitNoun, portName: port.name, portLabel: port.displayName }, ruleId: 'required-input-unfed', nodeId: node.id, portName: port.name, message: `${exitNoun} '${port.displayName}' is not connected` })
      }
      continue
    }

    const ownType = blockTypes.find((bt) => bt.blockTypeId === node.blockTypeId)
    const label = nodeLabel(node)
    errors.push(...runNodeRules(rules, { node, edges, blockType: ownType, label, resolveOutputType, portTypes }))
    const blockType = bound.get(node.id)
    if (!blockType) continue

    const reference = blockType.runsWorkflowFrom || undefined
    if (reference && edges.some((e) => e.targetNodeId === node.id && isActiveMapping(e, reference))) {
      errors.push({
        code: 'reference-input-mapped', ruleId: 'reference-input-mapped', nodeId: node.id, portName: reference,
        params: { nodeLabel: label, reference },
        message: `Node '${label}' takes the workflow it runs from its '${reference}' setting only; a mapping into it is refused`,
      })
    } else if (reference) {
      const value = own(node.configuration, reference)
      if (typeof value !== 'string' || value === '') {
        errors.push({ code: 'node-ports-unresolved', ruleId: 'node-ports-unresolved', params: { nodeLabel: label, reference }, nodeId: node.id, message: `Node '${label}' names no workflow to run in '${reference}'` })
      }
    }

    if (portsKnown) {
      for (const port of [...(blockType.inputs ?? []), ...dynamicPorts(node, blockType, INPUT_PORTS_KEY)]) {
        if (!port.isRequired || port.name === reference || isFed(node, port.name)) continue
        errors.push({ code: 'required-input-unfed', ruleId: 'required-input-unfed', params: { nodeLabel: label, portName: port.name, portLabel: port.displayName }, nodeId: node.id, portName: port.name, message: `Node '${label}' is missing required mapping for '${port.displayName}'` })
      }
    }

    if (signatureKnown) {
      for (const branch of Object.keys(node.subCanvases ?? {})) {
        if ((blockType.subCanvasNames ?? []).includes(branch)) continue
        errors.push({ code: 'sub-canvas-name-unknown', ruleId: 'sub-canvas-name-unknown', params: { nodeLabel: label, branchName: branch }, nodeId: node.id, portName: branch, message: `Node '${label}' has a branch '${branch}' its block does not declare` })
      }
    }

    for (const field of blockType.configurationSchema?.filter((f) => f.required) ?? []) {
      if (!isFilled(own(node.configuration, field.name))) {
        errors.push({ code: 'required-config-missing', ruleId: 'required-config-missing', params: { nodeLabel: label, fieldName: field.name, fieldLabel: field.label }, nodeId: node.id, portName: field.name, message: `Node '${label}' is missing required configuration field '${field.label}'` })
      }
    }

    const siteTerms = portsKnown ? configSiteTerms(node, blockType, portTypes) : new Map<string, PortTypeName | undefined>()
    for (const field of blockType.configurationSchema ?? []) {
      const value = own(node.configuration, field.name)
      if (!isConfigured(value) || isExpressionText(value)) continue
      if (field.type === 'Enum') {
        if (value === '') continue
        const refused = enumLiteralFinding(node, label, field.name, field.allowedValues ?? [], value)
        if (refused) errors.push(refused)
        continue
      }
      const type = siteTerms.has(field.name) ? siteTerms.get(field.name) : field.type === 'Int' ? 'Int' : field.type === 'Number' ? 'Number'
        : field.type === 'Boolean' ? 'Boolean' : 'String'
      if (type !== undefined && !portTypes.isLiteralOf(value, type)) {
        errors.push({
          code: 'literal-port-value-invalid', ruleId: 'literal-port-value-invalid', nodeId: node.id, portName: field.name,
          params: { nodeLabel: label, portName: field.name, expectedType: type },
          message: `Node '${label}' has a value for '${field.name}' that cannot be read as ${type}.`,
        })
      }
    }

    const fieldNames = new Set((blockType.configurationSchema ?? []).map((f) => f.name))
    for (const [name, type] of siteTerms) {
      const value = own(node.configuration, name)
      if (type === undefined || fieldNames.has(name) || !isConfigured(value) || isExpressionText(value)) continue
      if (!portTypes.isLiteralOf(value, type)) {
        errors.push({
          code: 'literal-port-value-invalid', ruleId: 'literal-port-value-invalid', nodeId: node.id, portName: name,
          params: { nodeLabel: label, portName: name, expectedType: String(type) },
          message: `Node '${label}' has a value for '${name}' that cannot be read as ${String(type)}.`,
        })
      }
    }

    for (const [key, kind] of RESILIENCE) {
      const value = own(node.configuration, key)
      if (!isConfigured(value)) continue
      if (!readsAs(value, kind)) {
        errors.push({ code: 'resilience-config-invalid', ruleId: 'resilience-config-invalid', params: { nodeLabel: label, key, value: JSON.stringify(value), expectation: READS_AS[kind] }, nodeId: node.id, portName: key, message: `Node '${label}' has '${key}' set to ${JSON.stringify(value)}, which the engine cannot read as ${READS_AS[kind]}` })
      } else if (key === 'timeoutSeconds' && (value as number) > LONGEST_ENGINE_WAIT_SECONDS) {
        errors.push({ code: 'resilience-config-invalid.timeout-too-long', ruleId: 'resilience-config-invalid', params: { nodeLabel: label, key, value: String(value), maxSeconds: LONGEST_ENGINE_WAIT_SECONDS }, nodeId: node.id, portName: key, message: `Node '${label}' sets '${key}' to ${String(value)}, and the longest deadline the engine applies is ${LONGEST_ENGINE_WAIT_SECONDS} seconds (one day)` })
      }
    }

    errors.push(...undeclaredPortShapeFindings(node, blockType, label))

    const staticOutputNames = new Set((blockType.outputs ?? []).map((o) => o.name))
    for (const port of (node.outputPorts ?? []).filter((p) => staticOutputNames.has(p.name))) {
      errors.push({
        code: 'declared-output-shadows-static', ruleId: 'declared-output-shadows-static', nodeId: node.id, portName: port.name,
        params: { nodeLabel: label, portName: port.name },
        message: `Node '${label}' declared output '${port.name}' shadows a static output — rename it to avoid collision`,
      })
    }
  }

  if (portsKnown && canvasPorts) errors.push(...wirePortFindings(nodes, edges, nodeLabel, bound, canvasPorts, workflowOutputParams, portTypes))
  return errors
}

type PortDeclarations = Pick<BlockTypeDescriptor, 'hasDynamicPorts' | 'supportsDeclaredOutputs' | 'subCanvasNames'>
const NO_DECLARATIONS: PortDeclarations = {}

function undeclaredPortShapeFindings(node: WorkflowNode, blockType: PortDeclarations, label: string): ValidationFinding[] {
  const findings: ValidationFinding[] = []
  if (!blockType.hasDynamicPorts) {
    for (const key of [INPUT_PORTS_KEY, OUTPUT_PORTS_KEY]) {
      const text = own(node.configuration, key)
      if (typeof text !== 'string' || parseDynamicPorts(text).length === 0) continue
      findings.push({
        code: 'port-list-without-dynamic-ports', ruleId: 'port-list-without-dynamic-ports', nodeId: node.id, portName: key,
        params: { nodeLabel: label, key },
        message: `Node '${label}' holds a port list under '${key}', and its block declares no dynamic ports`,
      })
    }
  }
  const declared = node.outputPorts ?? []
  if (declared.length === 0) return findings
  if (!blockType.supportsDeclaredOutputs) {
    findings.push({
      code: 'declared-outputs-unsupported', ruleId: 'declared-outputs-unsupported', nodeId: node.id,
      params: { nodeLabel: label },
      message: `Node '${label}' declares outputs, and its block supports none`,
    })
    return findings
  }
  for (const port of declared) {
    for (const branch of Object.keys(port.sources ?? {})) {
      if ((blockType.subCanvasNames ?? []).includes(branch)) continue
      findings.push({
        code: 'declared-output-source-unknown', ruleId: 'declared-output-source-unknown', nodeId: node.id, portName: port.name,
        params: { nodeLabel: label, portName: port.name, branchName: branch },
        message: `Declared output '${port.name}' on node '${label}' takes a source from '${branch}', which is no branch of its block`,
      })
    }
  }
  return findings
}

function errorPolicyFindings(node: WorkflowNode, label: string): ValidationFinding[] {
  const findings: ValidationFinding[] = []
  if (own(node.configuration, 'errorPolicy') !== undefined) {
    const at = { ruleId: 'error-policy-in-configuration', nodeId: node.id, portName: 'errorPolicy', params: { nodeLabel: label } }
    if (isBoundary(node)) {
      findings.push({
        ...at, code: 'error-policy-in-configuration.boundary',
        message: `Node '${label}' keeps an error policy in its configuration, where the run never reads it, and an entry or exit node has none: remove the key`,
      })
    } else {
      findings.push({
        ...at, code: 'error-policy-in-configuration',
        message: `Node '${label}' keeps its error policy in its configuration, where the run never reads it: set the policy on the node instead`,
      })
    }
  }
  if (node.errorPolicy?.routeToErrorPort === true) {
    findings.push({
      code: 'error-policy-route-unread', ruleId: 'error-policy-route-unread', nodeId: node.id, portName: 'errorPolicy',
      params: { nodeLabel: label },
      message: `Node '${label}' sets 'routeToErrorPort', which no run reads: remove it. A node offers '_error' under EmitError alone`,
    })
  }
  return findings
}

function wirePortFindings(
  nodes: readonly WorkflowNode[],
  edges: readonly WorkflowEdge[],
  label: (node: WorkflowNode) => string,
  bound: ReadonlyMap<string, BlockTypeDescriptor>,
  canvasPorts: CanvasPortNames,
  workflowOutputParams: readonly ExitPort[],
  portTypes: PortTypeRegistry,
): ValidationFinding[] {
  const findings: ValidationFinding[] = []
  const byId = new Map(nodes.map((n) => [n.id, n]))
  const trace: TraceCanvas = { nodes, edges, bound, entryPorts: canvasPorts.entry }
  const knows = (n: WorkflowNode) => {
    const blockType = bound.get(n.id)
    return !(blockType?.runsWorkflowFrom) && !hasMalformedStoredPortType(n, blockType)
  }
  for (const e of edges) {
    const source = byId.get(e.sourceNodeId)
    const target = byId.get(e.targetNodeId)
    if (!source || !target) continue
    if (!(isBoundary(source) || bound.has(source.id)) || !(isBoundary(target) || bound.has(target.id))) continue
    const outputs = knows(source) ? portNames(source, bound.get(source.id), 'outputs', canvasPorts, trace) : undefined
    const inputs = knows(target) ? portNames(target, bound.get(target.id), 'inputs', canvasPorts, trace) : undefined
    for (const [inputName, mapping] of Object.entries(e.mappings ?? {})) {
      if (!isActiveMapping(e, inputName)) continue
      const read = readMapping(mapping)
      const outputName = read.literal ? '' : read.source
      if (read.literal && inputs) {
        const targetType = bound.get(target.id)
        const effectivePort: Pick<BlockPort, 'name' | 'type'> | undefined = target.blockTypeId === WORKFLOW_OUTPUT_BLOCK_TYPE_ID
          ? workflowOutputParams.find((candidate) => candidate.name === inputName)
          : isBoundary(target) ? undefined
            : [...(targetType?.inputs ?? []), ...dynamicPorts(target, targetType, INPUT_PORTS_KEY)].find((candidate) => candidate.name === inputName)
        if (effectivePort && !isExpressionText(read.value) && isGradableTerm(effectivePort.type, portTypes) && !portTypes.isLiteralOf(read.value, effectivePort.type)) {
          const expectedType = String(effectivePort.type)
          findings.push({
            code: 'literal-port-value-invalid', ruleId: 'literal-port-value-invalid', nodeId: target.id, portName: inputName,
            params: { nodeLabel: label(target), portName: inputName, expectedType },
            message: `Node '${label(target)}' has a value for '${inputName}' that cannot be read as ${expectedType}.`,
          })
        } else if (effectivePort && !isExpressionText(read.value) && read.value !== '') {
          const enumField = targetType?.configurationSchema?.find((f) => f.type === 'Enum' && f.name === inputName)
          const refused = enumField && enumLiteralFinding(target, label(target), inputName, enumField.allowedValues ?? [], read.value)
          if (refused) findings.push(refused)
        }
      }
      if (outputs && !read.literal && !outputs.has(outputName)) {
        findings.push({
          code: 'wire-port-unknown.output', ruleId: 'wire-port-unknown', params: { nodeLabel: label(source), portName: outputName, edgeId: e.id ?? '' }, nodeId: source.id, portName: outputName, edgeId: e.id,
          message: `Node '${label(source)}' has no output '${outputName}' for connection '${e.id ?? ''}' to map`,
        })
      }
      if (inputs && !inputs.has(inputName)) {
        findings.push({
          code: 'wire-port-unknown.input', ruleId: 'wire-port-unknown', params: { nodeLabel: label(target), portName: inputName, edgeId: e.id ?? '' }, nodeId: target.id, portName: inputName, edgeId: e.id,
          message: `Node '${label(target)}' has no input '${inputName}' for connection '${e.id ?? ''}' to feed`,
        })
      }
    }
  }
  return findings
}
