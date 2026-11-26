import type { BlockTypeDescriptor } from '../model/blockType.js'
import type { CanvasPathSegment, ValidationFinding, WorkflowDefinition, WorkflowParameter } from '../model/workflow.js'
import type { StoredDocumentFault, StoredPortTypeSite } from '../model/storedDocument.js'
import { describeStoredDocumentFault, readParameterList, readStoredDefinition } from '../model/storedDocument.js'
import { describePortTypeSyntaxError, parsePortTypeName } from '../portTypes/grammar.js'

const ORIGIN: Record<StoredPortTypeSite['site'], (site: StoredPortTypeSite) => string> = {
  InputParameter: () => 'a workflow input parameter',
  OutputParameter: () => 'a workflow output parameter',
  DynamicInputPort: (site) => `a dynamic input port of node '${site.nodeId ?? ''}'`,
  DynamicOutputPort: (site) => `a dynamic output port of node '${site.nodeId ?? ''}'`,
  DeclaredOutput: (site) => `a declared output of node '${site.nodeId ?? ''}'`,
  TypeArgument: (site) => `a type argument of node '${site.nodeId ?? ''}'`,
  ProjectionType: (site) => `the projection type of input '${site.portName}' of node '${site.nodeId ?? ''}'`,
}

type Read = { ok: true; sites: StoredPortTypeSite[] } | { ok: false; fault: StoredDocumentFault }

function canvasPathAt(definition: WorkflowDefinition, location: string): CanvasPathSegment[] {
  const tokens = location.split('/').slice(1).map((t) => t.replaceAll('~1', '/').replaceAll('~0', '~'))
  const path: CanvasPathSegment[] = []
  let canvas: WorkflowDefinition | undefined = definition
  for (let i = 0; canvas && tokens[i] === 'nodes' && tokens[i + 2] === 'subCanvases' && i + 4 < tokens.length; i += 4) {
    const owner: WorkflowDefinition['nodes'][number] | undefined = canvas.nodes[Number(tokens[i + 1])]
    const branchName = tokens[i + 3] as string
    canvas = owner?.subCanvases?.[branchName]
    if (owner && canvas) path.push({ nodeId: owner.id, branchName })
  }
  return path
}

/** The one finding a refused stored document reports, as the backend's: a reader rule that is a finding code
 * (`duplicate-node-id`, `duplicate-edge-id`, `stored-key-unknown`, `dotted-name`) under its own code, any other under `unreadable`. */
export function storedDocumentFaultFinding(
  fault: StoredDocumentFault,
  subject: string,
  unreadable: 'definition-unreadable' | 'signature-unreadable',
  options: { canvasPath?: readonly CanvasPathSegment[]; labelOf?: (blockTypeId: string) => string } = {},
): ValidationFinding {
  if (fault.rule === 'duplicate-node-id') {
    const duplicateId = fault.nodeId ?? ''
    const location = (fault.canvasPath ?? []).reduce((where, segment, i) => {
      const type = fault.canvasBlockTypeIds?.[i] ?? segment.nodeId
      return ` in branch '${segment.branchName}' of '${options.labelOf?.(type) ?? type}'${where}`
    }, '')
    return {
      code: 'duplicate-node-id', ruleId: 'duplicate-node-id', params: { duplicateId, location }, nodeId: duplicateId,
      ...(fault.canvasPath ? { canvasPath: fault.canvasPath } : {}),
      message: `Node id '${duplicateId}' is used by more than one node${location}`,
    }
  }
  if (fault.rule === 'duplicate-edge-id') {
    const described = describeStoredDocumentFault(fault)
    return { code: 'duplicate-edge-id', ruleId: 'duplicate-edge-id', params: { fault: described }, nodeId: fault.nodeId,
      ...(fault.canvasPath ? { canvasPath: fault.canvasPath } : {}), message: `Connection id is duplicated: ${described}.` }
  }
  const code = fault.rule === 'stored-key-unknown' || fault.rule === 'dotted-name' || fault.rule === 'error-policy-route-unread' || fault.rule === 'output-mappings-unread'
    ? fault.rule : unreadable
  const described = describeStoredDocumentFault(fault)
  return {
    code, ruleId: code, params: { subject, fault: described }, nodeId: fault.nodeId,
    ...(fault.rule === 'error-policy-route-unread' ? { portName: 'errorPolicy' } : {}),
    ...(fault.rule === 'output-mappings-unread' ? { portName: 'outputMappings' } : {}),
    ...((options.canvasPath ?? fault.canvasPath) ? { canvasPath: options.canvasPath ?? fault.canvasPath } : {}),
    message: `${subject} cannot be read: ${described}.`,
  }
}

/** Grades malformed stored type names and unknown type-argument keys, preserving their canvas locations. */
export function validatePortTypeNames(input: {
  definition: WorkflowDefinition
  workflowInputParams: readonly WorkflowParameter[]
  workflowOutputParams: readonly WorkflowParameter[]
  /** Names a branch's container in a duplicate-node-id finding; its block type id without it. */
  blockTypes?: readonly BlockTypeDescriptor[]
}): ValidationFinding[] {
  const labelOf = (type: string) => input.blockTypes?.find((b) => b.blockTypeId === type)?.displayName ?? type
  const findingsBySite = (read: Read, unreadable: string, ruleId: 'signature-unreadable' | 'definition-unreadable', pathAt: (location: string) => CanvasPathSegment[]): ValidationFinding[] => {
    if (!read.ok) return [storedDocumentFaultFinding(read.fault, unreadable, ruleId, { canvasPath: pathAt(read.fault.location), labelOf })]
    return read.sites.flatMap((site) => {
      const path = pathAt(site.location)
      const siteFindings: ValidationFinding[] = []
      let unknownArgument: ValidationFinding | undefined
      if (site.site === 'TypeArgument' && site.nodeId !== null) {
        let canvas = input.definition
        for (const segment of path) {
          const owner = canvas.nodes.find((entry) => entry.id === segment.nodeId)
          canvas = owner?.subCanvases?.[segment.branchName] ?? { nodes: [], edges: [] }
        }
        const node = canvas.nodes.find((entry) => entry.id === site.nodeId)
        const block = input.blockTypes?.find((entry) => entry.blockTypeId === node?.blockTypeId)
        if (block && !(block.typeParameters ?? []).some((parameter) => parameter.name === site.portName)) {
          unknownArgument = { code: 'type-argument-unknown', ruleId: 'type-argument-unknown', params: { name: site.portName, blockType: block.displayName }, nodeId: site.nodeId, portName: site.portName,
            canvasPath: path, message: `Type argument '${site.portName}' names no type parameter of block '${block.displayName}'.` }
        }
      }
      const parsed = parsePortTypeName(site.rawType)
      if (!parsed.ok) {
        const code = parsed.fault === 'MapKey' ? 'port-type-map-key' : 'port-type-malformed'
        const what = parsed.fault === 'MapKey' ? 'has a Map type whose key is not String' : 'is not a well-formed name'
        const detail = describePortTypeSyntaxError(site.rawType, parsed)
        siteFindings.push({ code, ruleId: code, params: { portName: site.portName, rawType: site.rawType, detail, origin: ORIGIN[site.site](site) },
          nodeId: site.nodeId, portName: site.portName, canvasPath: path, message: `Port type of '${site.portName}' ${what}: ${detail} It is ${ORIGIN[site.site](site)}.` })
      }
      if (unknownArgument) siteFindings.push(unknownArgument)
      return siteFindings
    })
  }
  return [
    ...findingsBySite(readParameterList(input.workflowInputParams, 'input'), "The workflow's declared input parameters", 'signature-unreadable', () => []),
    ...findingsBySite(readParameterList(input.workflowOutputParams, 'output'), "The workflow's declared output parameters", 'signature-unreadable', () => []),
    ...findingsBySite(readStoredDefinition(input.definition), 'The workflow definition', 'definition-unreadable', (at) => canvasPathAt(input.definition, at)),
  ]
}
