/** The endpoint description the server publishes, as this package reads it. */
export interface Surface {
  readonly formatVersion: number
  readonly source: string
  readonly endpoints: readonly SurfaceEndpoint[]
  readonly types: readonly SurfaceType[]
}

export interface SurfaceEndpoint {
  readonly operation: string
  readonly method: string
  readonly path: string
  readonly pathParameters: readonly SurfaceParameter[]
  readonly queryParameters: readonly SurfaceParameter[]
  readonly requestBody?: { readonly type: string; readonly required: boolean }
  readonly responses: readonly { readonly status: number; readonly type?: string }[]
}

export interface SurfaceParameter {
  readonly name: string
  readonly type: string
  readonly required: boolean
}

export interface SurfaceType {
  readonly name: string
  readonly properties: readonly { readonly name: string; readonly type: string }[]
}

/** The format this package knows how to read. A document that predates it describes something else. */
export const SUPPORTED_FORMAT_VERSION = 1

/**
 * Reads an unvalidated document as a `Surface`, refusing anything that is not one. The generator is fed
 * from a file written by another repository, so the shape has to be established before it is trusted.
 */
export function parseSurface(document: unknown): Surface {
  const surface = document as Surface
  if (typeof surface !== 'object' || surface === null)
    throw new Error('Surface document is not an object')
  if (surface.formatVersion !== SUPPORTED_FORMAT_VERSION)
    throw new Error(
      `Surface format ${String(surface.formatVersion)} is not ${SUPPORTED_FORMAT_VERSION}; ` +
        'this generator would read fields that may have changed meaning',
    )
  if (!Array.isArray(surface.endpoints) || surface.endpoints.length === 0)
    throw new Error('Surface document declares no endpoints')
  if (!Array.isArray(surface.types)) throw new Error('Surface document declares no types')
  return surface
}
