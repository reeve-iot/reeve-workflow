export const OPERATOR_HEADER = 'X-Dispatch-Operator'

export const DEFAULT_OPERATOR_ID = '2f9a5c14-6b3d-4e07-9a58-0c7d1e2b4f36'

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

export function operatorId(search: string = globalThis.location.search): string {
  const asked = new URLSearchParams(search).get('operator')
  return asked !== null && GUID.test(asked) ? asked : DEFAULT_OPERATOR_ID
}

export function apiBaseUrl(search: string = globalThis.location.search): string {
  return new URLSearchParams(search).get('api') ?? globalThis.location.origin
}
