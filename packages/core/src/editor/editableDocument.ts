import type { SavedWorkflowDefinition, SavedWorkflowEdge, SavedWorkflowNode, WorkflowDefinition, WorkflowEdge, WorkflowNode } from '../model/workflow.js'

/** The editor's refusal of a document the stored-document reader accepted: a member it cannot draw. `location` is an
 * RFC 6901 pointer to the member, `nodeId` or `edgeId` its node or edge; a run reads the document as it is. */
export interface UndrawableDocumentFault {
  rule: 'undrawable'
  location: string
  detail: string
  nodeId: string | null
  edgeId: string | null
}

type Plain = Record<string, unknown>

const isObject = (value: unknown): value is Plain => typeof value === 'object' && value !== null && !Array.isArray(value)
const has = (value: Plain, key: string): boolean => Object.hasOwn(value, key)
const escape = (key: string): string => key.replaceAll('~', '~0').replaceAll('/', '~1')
const named = (value: unknown): string => (typeof value === 'string' ? `'${value}'` : 'with no id')
const idOf = (value: unknown): string | null => (typeof value === 'string' ? value : null)
const list = (value: unknown): unknown[] => (Array.isArray(value) ? value : [])

/** The first member of `definition` the editor refuses to open, canvas by canvas: nodes, their branches, then edges. */
export function undrawableMember(definition: WorkflowDefinition): UndrawableDocumentFault | null {
  return canvasFault(definition as unknown as Plain, '')
}

function canvasFault(canvas: Plain, at: string): UndrawableDocumentFault | null {
  for (const [i, node] of list(canvas['nodes']).entries()) {
    const found = isObject(node) ? nodeFault(node, `${at}/nodes/${String(i)}`) : null
    if (found) return found
  }
  for (const [i, edge] of list(canvas['edges']).entries()) {
    const found = isObject(edge) ? edgeFault(edge, `${at}/edges/${String(i)}`) : null
    if (found) return found
  }
  return null
}

function nodeFault(node: Plain, at: string): UndrawableDocumentFault | null {
  const refuse = (member: string, detail: string): UndrawableDocumentFault =>
    ({ rule: 'undrawable', location: `${at}/${member}`, detail: `the node ${named(node['id'])} ${detail}`, nodeId: idOf(node['id']), edgeId: null })
  const position = node['position']
  if (!isObject(position)) return refuse('position', "has no 'position', and the editor places no node at a default")
  for (const axis of ['x', 'y']) {
    if (!has(position, axis)) return refuse(`position/${axis}`, `has no 'position/${axis}', and the editor places no node at a default`)
  }
  for (const [name, sub] of Object.entries(isObject(node['subCanvases']) ? node['subCanvases'] : {})) {
    const found = isObject(sub) ? canvasFault(sub, `${at}/subCanvases/${escape(name)}`) : null
    if (found) return found
  }
  return null
}

function edgeFault(edge: Plain, at: string): UndrawableDocumentFault | null {
  const mappings = edge['mappings']
  if (!Array.isArray(mappings) || mappings.length === 0) return null
  return {
    rule: 'undrawable', location: `${at}/mappings`, nodeId: null, edgeId: idOf(edge['id']),
    detail: `the edge ${named(edge['id'])} holds its mappings in the array form, which the editor does not draw`,
  }
}

/** The document in the editor's form: an absent or null configuration, node list, edge list or mapping set, and an
 * empty mapping array, held as empty. `restoredDefinition` writes each back as it was loaded. */
export function editorForm(definition: WorkflowDefinition): WorkflowDefinition {
  return { nodes: list(definition.nodes).map((n) => editorNode(n as WorkflowNode)), edges: list(definition.edges).map((e) => editorEdge(e as WorkflowEdge)) }
}

function editorNode(node: WorkflowNode): WorkflowNode {
  const held: WorkflowNode = { ...node, configuration: (node.configuration as WorkflowNode['configuration'] | null) ?? {} }
  if (isObject(node.subCanvases)) {
    held.subCanvases = Object.fromEntries(Object.entries(node.subCanvases).map(([name, sub]) => [name, editorForm(sub)]))
  }
  return held
}

const editorEdge = (edge: WorkflowEdge): WorkflowEdge => ({ ...edge, mappings: isObject(edge.mappings) ? edge.mappings : {} })

/** Writes `current` with canonical edge and mapping forms; an empty passthrough is omitted.
 * Nodes and edges pair with loaded ones by id, then by order among one id. */
export function restoredDefinition(current: WorkflowDefinition, loaded: WorkflowDefinition | null): SavedWorkflowDefinition {
  return restoredCanvas(current, isObject(loaded) ? (loaded as unknown as Plain) : undefined)
}

function pairing(loaded: unknown): (id: unknown) => Plain | undefined {
  const byId = new Map<unknown, Plain[]>()
  for (const item of list(loaded)) {
    if (!isObject(item)) continue
    const same = byId.get(item['id']) ?? []
    same.push(item)
    byId.set(item['id'], same)
  }
  return (id) => byId.get(id)?.shift()
}

function write(target: Plain, key: string, value: unknown, empty: boolean, loaded: Plain | undefined): void {
  if (empty && loaded && !has(loaded, key)) delete target[key]
  else if (empty && loaded && (loaded[key] === null || Array.isArray(loaded[key]))) target[key] = loaded[key] === null ? null : []
  else target[key] = value
}

function restoredCanvas(canvas: WorkflowDefinition, loaded: Plain | undefined): SavedWorkflowDefinition {
  const loadedNode = pairing(loaded?.['nodes'])
  const nodes = canvas.nodes.map((n) => restoredNode(n, loadedNode(n.id)))
  const edges: SavedWorkflowEdge[] = canvas.edges.map((e) => {
    const out: Plain = { ...e }
    out['mappings'] = isObject(e.mappings) ? e.mappings : {}
    return out as unknown as SavedWorkflowEdge
  })
  const out: Plain = { ...(loaded ?? {}) }
  write(out, 'nodes', nodes, nodes.length === 0, loaded)
  out['edges'] = edges
  return out as SavedWorkflowDefinition
}

function restoredNode(node: WorkflowNode, loaded: Plain | undefined): SavedWorkflowNode {
  const out: Plain = { ...node }
  write(out, 'configuration', node.configuration, Object.keys(node.configuration).length === 0, loaded)
  if (node.passthrough?.length === 0) delete out['passthrough']
  if (isObject(node.subCanvases)) {
    const loadedSubs = isObject(loaded?.['subCanvases']) ? loaded['subCanvases'] : {}
    out['subCanvases'] = Object.fromEntries(Object.entries(node.subCanvases).map(([name, sub]) =>
      [name, restoredCanvas(sub, has(loadedSubs, name) && isObject(loadedSubs[name]) ? loadedSubs[name] : undefined)]))
  }
  return out as unknown as SavedWorkflowNode
}
