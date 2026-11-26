import { afterEach, describe, expect, it, vi } from 'vitest'
import { createDefaultId } from './createId.js'

const UUID_V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('createDefaultId', () => {
  it('produces a well-formed v4 uuid', () => {
    expect(createDefaultId()).toMatch(UUID_V4)
  })

  it('produces a different id each time', () => {
    expect(new Set(Array.from({ length: 50 }, createDefaultId)).size).toBe(50)
  })

  it('still works where crypto.randomUUID is missing, which is every insecure context', () => {
    vi.stubGlobal('crypto', { getRandomValues: (a: Uint8Array) => a.fill(7) })
    expect(createDefaultId()).toMatch(UUID_V4)
  })

  it('still works with no web crypto at all', () => {
    vi.stubGlobal('crypto', undefined)
    expect(createDefaultId()).toMatch(UUID_V4)
  })
})
