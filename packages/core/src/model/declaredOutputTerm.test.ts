import { describe, expect, it } from 'vitest'
import { declaredOutputTerm } from './blockType.js'

describe('declaredOutputTerm', () => {
  it('is the stored type on a block that does not collect', () => {
    expect(declaredOutputTerm({}, { type: 'Number' })).toBe('Number')
    expect(declaredOutputTerm({ declaredOutputsCollect: false }, { type: 'Number' })).toBe('Number')
  })

  it('is Collection<T?> on a collecting block, and Collection<T> when T is already nullable', () => {
    expect(declaredOutputTerm({ declaredOutputsCollect: true }, { type: 'Number' })).toBe('Collection<Number?>')
    expect(declaredOutputTerm({ declaredOutputsCollect: true }, { type: 'Number?' })).toBe('Collection<Number?>')
  })
})
