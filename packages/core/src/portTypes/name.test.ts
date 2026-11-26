import { describe, expect, it } from 'vitest'
import type { PortTypeName } from './name.js'
import { createPortTypeRegistry } from './registry.js'

declare module './name.js' {
  interface PortTypeVocabulary {
    Device: true
  }
}

describe('PortTypeName', () => {
  it('accepts a name a host contributed by declaration merging', () => {
    const declared: PortTypeName = 'Device'
    expect(createPortTypeRegistry([{ name: declared }]).has('Device')).toBe(true)
  })

  it('stays open, so a graph naming a type this build never heard of still loads', () => {
    const unheardOf: PortTypeName = 'TypeFromANewerHost'
    expect(createPortTypeRegistry([]).normalize(unheardOf)).toBe('TypeFromANewerHost')
  })
})
