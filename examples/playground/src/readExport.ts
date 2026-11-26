import { readDeclaredPorts, readWorkflowDefinition } from '@reeve-iot/workflow-adapters'
import type { PortInfo, PortTypeRegistry, StoredDefinitionRead, StoredDocumentFault } from '@reeve-iot/workflow-core'
import { describeRepeatedMember, findRepeatedMember } from '@reeve-iot/workflow-core'
import type { WorkflowExportDto } from '@reeve-iot/workflow-client'

export interface ReadDocuments {
  readonly definition: StoredDefinitionRead
  readonly inputParams: readonly PortInfo[]
  readonly outputParams: readonly PortInfo[]
  readonly fault: StoredDocumentFault | null
}

export interface OpenedExport extends ReadDocuments {
  readonly dto: WorkflowExportDto
  readonly name: string
}

export type OpenRead = { ok: true; opened: OpenedExport } | { ok: false; error: string }

function carriesAnExport(value: unknown): value is WorkflowExportDto {
  if (typeof value !== 'object' || value === null) return false
  const candidate = value as Record<string, unknown>
  const definition = candidate['definitionJson']
  return (typeof definition === 'string' || definition === null || definition === undefined) && typeof candidate['name'] === 'string'
}

export interface DocumentTexts {
  readonly definitionJson: string | null
  readonly inputParametersJson: string | null
  readonly outputParametersJson: string | null
}

export function readDocuments(texts: DocumentTexts, portTypes: PortTypeRegistry): ReadDocuments {
  const inputs = readDeclaredPorts(texts.inputParametersJson, portTypes)
  const outputs = readDeclaredPorts(texts.outputParametersJson, portTypes)
  const definition = readWorkflowDefinition(texts.definitionJson)
  return {
    definition,
    inputParams: inputs.ok ? inputs.ports : [],
    outputParams: outputs.ok ? outputs.ports : [],
    fault: (definition.ok ? null : definition.fault)
      ?? (inputs.ok ? null : inputs.fault)
      ?? (outputs.ok ? null : outputs.fault),
  }
}

export function readExport(text: string, portTypes: PortTypeRegistry): OpenRead {
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch (error) {
    return { ok: false, error: `This file is not JSON: ${error instanceof Error ? error.message : String(error)}.` }
  }
  const repeat = findRepeatedMember(text)
  if (repeat !== undefined) return { ok: false, error: `This file is ambiguous JSON: ${describeRepeatedMember(repeat)}.` }
  if (!carriesAnExport(parsed)) {
    return { ok: false, error: 'This file is not a WorkflowExportDto: it carries no name, or a definitionJson that is neither text nor null.' }
  }

  return { ok: true, opened: { dto: parsed, name: parsed.name, ...readDocuments(parsed, portTypes) } }
}
