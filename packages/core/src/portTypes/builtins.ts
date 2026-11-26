import type { PortTypeDefinition } from './types.js'

/** The scalar vocabulary every host shares. Domain types (Device, Room, Scene, …) are NOT here — a host
 * registers those itself; that separation is the whole point of the package. */
export const BUILTIN_PORT_TYPES: readonly PortTypeDefinition[] = Object.freeze([
  { name: 'String', aliases: ['string'] },
  { name: 'Number', aliases: ['number'] },
  { name: 'Int', aliases: ['int'], widensTo: ['Number'] },
  { name: 'Boolean', aliases: ['boolean', 'bool'] },
  { name: 'DateTime', aliases: ['datetime'] },
  { name: 'Json', aliases: ['json'] },
])
