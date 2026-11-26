import { describe, expect, it, vi } from 'vitest'
import { createReadCache } from './cache.js'

const EMPTY: readonly string[] = []

const settled = () => new Promise((resolve) => { setTimeout(resolve, 0) })

describe('createReadCache', () => {
  it('answers the empty value for an unknown key and starts one load', async () => {
    const load = vi.fn(async (key: string) => [key])
    const cache = createReadCache<readonly string[]>(load, EMPTY)

    expect(cache.read('a')).toBe(EMPTY)
    await settled()

    expect(cache.read('a')).toEqual(['a'])
    expect(load).toHaveBeenCalledTimes(1)
  })

  it('starts nothing for a key whose load is still in flight', () => {
    const load = vi.fn(async () => [])
    const cache = createReadCache<readonly string[]>(load, EMPTY)

    cache.read('a')
    cache.read('a')
    cache.read('a')

    expect(load).toHaveBeenCalledTimes(1)
  })

  it('notifies subscribers once a load settles, and stops when they unsubscribe', async () => {
    const cache = createReadCache<readonly string[]>(async (key) => [key], EMPTY)
    const listener = vi.fn()
    const stop = cache.subscribe(listener)

    cache.read('a')
    await settled()
    expect(listener).toHaveBeenCalledTimes(1)

    stop()
    cache.read('b')
    await settled()
    expect(listener).toHaveBeenCalledTimes(1)
  })

  it('remembers a failure as the empty value and does not ask again', async () => {
    const load = vi.fn(async () => { throw new Error('nope') })
    const onError = vi.fn()
    const cache = createReadCache<readonly string[]>(load, EMPTY, onError)

    cache.read('a')
    await settled()

    expect(cache.read('a')).toBe(EMPTY)
    expect(load).toHaveBeenCalledTimes(1)
    expect(onError).toHaveBeenCalledWith('a', expect.any(Error))
  })

  it('drops a load that was in flight when the cache was invalidated', async () => {
    let release: (value: readonly string[]) => void = () => undefined
    const cache = createReadCache<readonly string[]>(
      async () => new Promise<readonly string[]>((resolve) => { release = resolve }),
      EMPTY,
    )

    cache.read('a')
    cache.invalidate()
    release(['stale'])
    await settled()

    expect(cache.read('a')).toBe(EMPTY)
  })

  it('loads again after invalidation', async () => {
    const load = vi.fn(async (key: string) => [key])
    const cache = createReadCache<readonly string[]>(load, EMPTY)

    cache.read('a')
    await settled()
    cache.invalidate()
    expect(cache.read('a')).toBe(EMPTY)
    await settled()

    expect(cache.read('a')).toEqual(['a'])
    expect(load).toHaveBeenCalledTimes(2)
  })

  it('notifies subscribers on invalidation, so a render in flight sees the empty list', () => {
    const cache = createReadCache<readonly string[]>(async (key) => [key], EMPTY)
    const listener = vi.fn()
    cache.subscribe(listener)

    cache.invalidate()

    expect(listener).toHaveBeenCalledTimes(1)
  })
})
