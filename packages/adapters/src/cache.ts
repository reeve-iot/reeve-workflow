/**
 * A read-through cache whose reads are safe to make during render: an unknown key returns the empty
 * value and starts one load, and the same key asked again while that load is in flight starts nothing.
 */
export interface ReadCache<T> {
  /** The value for a key, and the load that fills it if nobody has asked yet. */
  read: (key: string) => T
  /** Notified whenever a load settles. */
  subscribe: (listener: () => void) => () => void
  /** Forgets everything, so the next read loads again. */
  invalidate: () => void
}

/**
 * `empty` is returned for a key that is not loaded yet and for one whose load failed, and it must be a
 * stable value: a caller reading during render compares identities to decide whether to re-render.
 */
export function createReadCache<T>(
  load: (key: string) => Promise<T>,
  empty: T,
  onError?: (key: string, error: unknown) => void,
): ReadCache<T> {
  let values = new Map<string, T>()
  let inFlight = new Set<string>()
  const listeners = new Set<() => void>()
  let generation = 0

  const settle = (key: string, value: T, startedAt: number): void => {
    if (startedAt !== generation) return
    values.set(key, value)
    inFlight.delete(key)
    for (const listener of [...listeners]) listener()
  }

  return {
    read(key) {
      const known = values.get(key)
      if (known !== undefined) return known
      if (!inFlight.has(key)) {
        inFlight.add(key)
        const startedAt = generation
        void load(key).then(
          (value) => { settle(key, value, startedAt) },
          (error: unknown) => { onError?.(key, error); settle(key, empty, startedAt) },
        )
      }
      return empty
    },
    subscribe(listener) {
      listeners.add(listener)
      return () => { listeners.delete(listener) }
    },
    invalidate() {
      generation += 1
      values = new Map()
      inFlight = new Set()
      for (const listener of [...listeners]) listener()
    },
  }
}
