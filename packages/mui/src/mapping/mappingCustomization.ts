import { createContext, useContext } from 'react'
import type { ReactNode } from 'react'
import type { MappingProjection, PortTypeName } from '@reeve-iot/workflow-core'

interface ProjectionControlBase {
  edgeId: string
  targetNodeId: string
  inputName: string
  sourceNodeId: string
  targetType?: PortTypeName
}

/** A direct mapping that reads a `Json` or `Json?` output: the wire is a projection wire. */
export interface WireProjectionControlContext extends ProjectionControlBase {
  placement: 'wire'
  sourceOutputName: string
  /** The source port's declared term, `Json` or `Json?`. A stored projection on another source also opens the control so it
   * can be cleared; the term is then that source's, or undefined where the source declares no such output. */
  sourceType?: PortTypeName
  /** The projection the mapping stores, or undefined. */
  projection: MappingProjection | undefined
  /** Sets the projection, or clears it with null. The mapping dialog holds it until the author saves, through the rules of
   * the store's `setMappingProjection`; only the server's draft analysis grades the path and the type. */
  onChange: (projection: MappingProjection | null) => void
}

/** An input whose mapping is an expression, on a wire whose source offers `Json` outputs. */
export interface ExpressionProjectionControlContext extends ProjectionControlBase {
  placement: 'expression'
  /** The source's outputs declared `Json` or `Json?`. */
  jsonOutputs: readonly { name: string; type: PortTypeName }[]
  /** The expression as stored, without its `expr:` prefix. */
  expressionBody: string
  /** Replaces the expression body; the mapping dialog stores it as the literal. */
  onExpressionChange: (body: string) => void
}

/** What a host's projection control is handed. */
export type ProjectionControlContext = WireProjectionControlContext | ExpressionProjectionControlContext

/** How a host reaches into the mapping dialog. Without it the dialog draws no projection control: the control is the
 * host's to build. */
export interface MappingCustomization {
  /** Called inside the mapping dialog on a direct mapping from a `Json` output (placement `wire`) and on an
   * expression mapping whose wire has `Json` outputs (placement `expression`). Return the host's control (a path field,
   * a type picker, a paste-sample assist), or null for none. */
  renderProjectionControl?: (context: ProjectionControlContext) => ReactNode | null
}

const NONE: MappingCustomization = {}

/** Provide a `MappingCustomization` above the mapping dialog to place the host's projection control in it. */
export const MappingCustomizationContext = createContext<MappingCustomization>(NONE)

/** The `MappingCustomization` in effect; an empty one where no provider is mounted. */
export function useMappingCustomization(): MappingCustomization {
  return useContext(MappingCustomizationContext)
}
