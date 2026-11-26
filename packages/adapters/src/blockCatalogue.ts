import { useEffect, useMemo, useSyncExternalStore } from 'react'
import type {
  BlockPortResponse,
  WorkflowBlockTypeResponse,
  WorkflowClient,
} from '@reeve-iot/workflow-client'
import {
  BLOCK_PORT_DECLARATION_INVALID,
  BlockPortDeclarationInvalidError,
  describePortTypeSyntaxError,
  parsePortTerm,
  usePortTypes,
  validationMessage,
} from '@reeve-iot/workflow-core'
import type { BlockPort, BlockTypeDescriptor, PortTypeRegistry, ValidationFinding } from '@reeve-iot/workflow-core'
import { createReadCache } from './cache.js'

const NONE: readonly BlockTypeDescriptor[] = []
const ALL = 'all'

function assertNoRelation(port: object, owner: string): void {
  if ('typeFrom' in port && port.typeFrom != null) {
    throw new Error(`Port '${owner}' declares a type relation, which this editor's block-catalogue format does not define: the server publishes a block catalogue this editor cannot type`)
  }
}

function assertNoTextPolicy(entry: object, owner: string): void {
  if ('textPolicy' in entry) {
    throw new Error(`'${owner}' declares a text policy, which this editor's block-catalogue format does not define: the server publishes a block catalogue this editor cannot grade literals for`)
  }
}

function assertDeclaredTermsParse(response: WorkflowBlockTypeResponse): void {
  const parse = (where: string, type: string): void => {
    const parsed = parsePortTerm(type, 'declaration')
    if (!parsed.ok) {
      throw new BlockPortDeclarationInvalidError(
        response.blockTypeId, where, `has the type '${type}', which is not a declaration term: ${describePortTypeSyntaxError(type, parsed)}`,
      )
    }
  }
  const ports = (list: readonly BlockPortResponse[], prefix: string): void => {
    for (const port of list) {
      const where = `${prefix}port '${port.name}'`
      parse(where, port.type)
      for (const variable of port.scopeVariables) parse(`${where} scope variable '${variable.name}'`, variable.type)
    }
  }
  ports(response.inputs, '')
  ports(response.outputs, '')
  for (const [branch, list] of Object.entries(response.subCanvasInputs)) ports(list, `sub-canvas '${branch}' `)
  for (const [branch, list] of Object.entries(response.subCanvasOutputs)) ports(list, `sub-canvas '${branch}' `)
}

function readPorts(ports: readonly BlockPortResponse[], normalize: (type: string) => string): BlockPort[] {
  return ports.map((p) => {
    if (p.name.includes('.')) throw new Error(`Port name '${p.name}' contains a dot (rule dotted-name).`)
    assertNoTextPolicy(p, p.name)
    assertNoRelation(p, p.name)
    return {
      name: p.name,
      displayName: p.displayName,
      type: normalize(p.type),
      isRequired: p.isRequired,
      allowedValues: p.allowedValues === null ? null : [...p.allowedValues],
      ...(p.scopeVariables.length === 0 ? {} : { scopeVariables: p.scopeVariables.map((v) => ({ name: v.name, type: normalize(v.type) })) }),
      ...(p.secretValues ? { secretValues: true } : {}),
    }
  })
}

function readSubCanvases(
  ports: Readonly<Record<string, readonly BlockPortResponse[]>>,
  normalize: (type: string) => string,
): Record<string, BlockPort[]> {
  return Object.fromEntries(Object.entries(ports).map(([name, p]) => [name, readPorts(p, normalize)]))
}

/**
 * One block type as the editor reads it, with the input it runs a workflow from and the type parameters its
 * ports name. `outputSatisfyingBranchSets` is absent on purpose: nothing on the
 * wire declares it, and the store's own `adaptBlockType` is where a host says so.
 */
export function readBlockType(
  response: WorkflowBlockTypeResponse,
  portTypes: PortTypeRegistry,
): BlockTypeDescriptor {
  const descriptor = describe(response, (type) => portTypes.normalize(type))
  assertDeclaredTermsParse(response)
  return descriptor
}

function describe(response: WorkflowBlockTypeResponse, normalize: (type: string) => string): BlockTypeDescriptor {
  return {
    blockTypeId: response.blockTypeId,
    displayName: response.displayName,
    description: response.description,
    category: response.category,
    inputs: readPorts(response.inputs, normalize),
    outputs: readPorts(response.outputs, normalize),
    hasDynamicPorts: response.hasDynamicPorts,
    supportsDeclaredOutputs: response.supportsDeclaredOutputs,
    ...(response.declaredOutputsCollect === true ? { declaredOutputsCollect: true } : {}),
    configurationSchema: response.configurationSchema.map((f) => {
      assertNoTextPolicy(f, f.name)
      return {
        ...f,
        allowedValues: f.allowedValues ? [...f.allowedValues] : null,
      }
    }),
    subCanvasNames: [...response.subCanvasNames],
    subCanvasInputs: readSubCanvases(response.subCanvasInputs, normalize),
    subCanvasOutputs: readSubCanvases(response.subCanvasOutputs, normalize),
    ...(response.runsWorkflowFrom === null ? {} : { runsWorkflowFrom: response.runsWorkflowFrom }),
    ...(response.typeParameters.length === 0
      ? {}
      : { typeParameters: response.typeParameters.map((p) => ({ name: p.name, constraints: [...p.constraints] })) }),
  }
}

function readCatalogue(
  responses: readonly WorkflowBlockTypeResponse[],
  portTypes: PortTypeRegistry,
): { blockTypes: readonly BlockTypeDescriptor[]; refused: readonly (ValidationFinding | Error)[] } {
  if (responses.length === 0) return { blockTypes: NONE, refused: [] }
  const blockTypes: BlockTypeDescriptor[] = []
  const refused: (ValidationFinding | Error)[] = []
  for (const response of responses) {
    try {
      blockTypes.push(readBlockType(response, portTypes))
    } catch (error) {
      const why = error instanceof Error ? error.message : String(error)
      if (error instanceof BlockPortDeclarationInvalidError) {
        const finding: ValidationFinding = {
          nodeId: null,
          code: BLOCK_PORT_DECLARATION_INVALID,
          params: { blockTypeId: error.blockTypeId, where: error.where, reason: error.reason },
          message: why,
        }
        finding.message = validationMessage(finding)
        refused.push(finding)
      } else {
        refused.push(new Error(`Block type '${response.blockTypeId}' is left out of the palette: ${why}`, { cause: error }))
      }
    }
  }
  return { blockTypes, refused }
}

export interface BlockCatalogueSource {
  /** The block types the editor draws with. Empty on the first render and while the list is loading. A block type
   * this editor cannot read is left out and its validation finding is reported to `onError` once per load. */
  useBlockTypes: () => readonly BlockTypeDescriptor[]
  /** Drops the catalogue, so the next render loads it again — call it after a plugin is installed. */
  refresh: () => void
}

/**
 * The palette's contents, fetched once and shared. Feed the result to the store's `setBlockTypes`: an
 * editor whose catalogue is empty draws nothing at all, which is why this is the first seam to wire.
 */
export function createBlockCatalogueSource(
  client: WorkflowClient,
  options: { readonly onError?: (error: unknown) => void } = {},
): BlockCatalogueSource {
  const cache = createReadCache<readonly WorkflowBlockTypeResponse[]>(
    async () => client.getBlockTypes(),
    [],
    (_, error) => { options.onError?.(error) },
  )

  const snapshot = (): readonly WorkflowBlockTypeResponse[] => cache.read(ALL)
  const reported = new WeakSet<readonly WorkflowBlockTypeResponse[]>()

  return {
    useBlockTypes() {
      const portTypes = usePortTypes()
      const responses = useSyncExternalStore(cache.subscribe, snapshot, snapshot)
      const read = useMemo(() => readCatalogue(responses, portTypes), [responses, portTypes])
      useEffect(() => {
        if (read.refused.length === 0 || reported.has(responses)) return
        reported.add(responses)
        for (const finding of read.refused) options.onError?.(finding)
      }, [read, responses])
      return read.blockTypes
    },
    refresh: cache.invalidate,
  }
}
