import type { NodeValidationRule, PortTypeDefinition } from '@reeve-iot/workflow-core'

export const DISPATCH_PORT_TYPES: readonly PortTypeDefinition[] = Object.freeze([
  { name: 'Shipment', aliases: ['shipment'], displayName: 'Shipment' },
  { name: 'Carrier', aliases: ['carrier'], displayName: 'Carrier' },
  { name: 'Depot', aliases: ['depot'], displayName: 'Depot' },
  { name: 'HazardClass', aliases: ['hazardclass'], displayName: 'Hazard class', widensTo: ['Int', 'Number'] },
])

export interface Depot {
  code: string
  name: string
}

export const DEPOTS: readonly Depot[] = Object.freeze([
  { code: 'GDN', name: 'Gdańsk' },
  { code: 'WAW', name: 'Warszawa' },
  { code: 'KTW', name: 'Katowice' },
])

export const bondedDepotRule: NodeValidationRule = ({ node, blockType, label }) => {
  if (blockType?.blockTypeId !== 'Dispatch') return []
  const hazard = node.configuration?.HazardClass
  const depot = node.configuration?.Depot
  if (hazard === undefined || hazard === null || hazard === '' || Number(hazard) === 0) return []
  if (depot === 'GDN') return []
  return [{
    code: 'dispatch-bonded-depot',
    nodeId: node.id,
    message: `'${label}' dispatches hazard class ${String(hazard)} through ${String(depot ?? 'no depot')}; only GDN is bonded.`,
  }]
}
