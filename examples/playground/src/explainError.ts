import { WorkflowApiError } from '@reeve-iot/workflow-client'
import { WorkflowDocumentUnreadableError } from '@reeve-iot/workflow-mui'
import type { CompilationDiagnostic } from '@reeve-iot/workflow-client'

function detailOf(body: unknown): string {
  if (typeof body === 'string') return body
  if (typeof body !== 'object' || body === null) return ''

  const record = body as { detail?: unknown; diagnostics?: unknown }
  const diagnostics = Array.isArray(record.diagnostics)
    ? (record.diagnostics as readonly CompilationDiagnostic[]).map((d) => `${d.ruleId}: ${d.message}`)
    : []
  const detail = typeof record.detail === 'string' ? record.detail : ''
  const joined = detail !== '' ? `${detail} ${diagnostics.join(' ')}` : diagnostics.join(' ')
  return joined.trim() !== '' ? joined.trim() : JSON.stringify(body)
}

export function explain(error: unknown): string {
  if (error instanceof WorkflowApiError) {
    const detail = detailOf(error.body)
    return `${error.operation} answered ${String(error.status)}${detail === '' ? '' : `: ${detail}`}`
  }
  if (error instanceof WorkflowDocumentUnreadableError) return error.message
  return error instanceof Error ? error.message : String(error)
}
