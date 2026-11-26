import { collectedPortType } from '../portTypes/grammar.js'
import type { PortTypeName } from '../portTypes/name.js'

/** Every descriptor carries an index signature, so a host's own catalogue fields survive; an optional member the
 * endpoint can write as `null` admits it and reads it as absent, so a host may pass the response on as is. */

/** A variable an `Expression<T>` input's expression may read while its block evaluates it, as `${name}`: `name` is `item` or
 * `value`, and `type` is a declaration term that may name the block's type parameters (`$T`). */
export interface BlockScopeVariable {
  name: string
  type: string
}

export interface BlockPort {
  name: string
  displayName: string
  type: PortTypeName
  isRequired: boolean
  allowedValues?: string[] | null
  /** The variables the expression of an `Expression<T>` input may read; absent for any other port. */
  scopeVariables?: BlockScopeVariable[]
  /** The entries of this `Map<String,String>` input may be secrets, and its keys are field names. */
  secretValues?: boolean
  [key: string]: unknown
}

/** A type parameter a block declares: its port types name it as `$Name`, and the compiler binds it per node. The
 * `constraints` are capability names a bound term must implement; absent or empty admits any term. */
export interface BlockTypeParameter {
  name: string
  constraints?: string[]
}

export interface BlockConfigField {
  name: string
  label: string
  type: string
  required: boolean
  defaultValue?: string | null
  description?: string | null
  /** The names an `Enum` field accepts, in the order to list them; the stored value is one of these, as written. */
  allowedValues?: string[] | null
  [key: string]: unknown
}

/** What the editor needs to know about one kind of block. The host owns the catalogue; the library only
 * reads it. */
export interface BlockTypeDescriptor {
  blockTypeId: string
  displayName: string
  description?: string
  category?: string
  inputs: BlockPort[]
  outputs: BlockPort[]
  /** The block declares its ports in its own configuration rather than in this descriptor; the editor then
   * reads them from the node's `InputPorts` / `OutputPorts` configuration entries. */
  hasDynamicPorts?: boolean
  supportsDeclaredOutputs?: boolean
  /** The block runs its one sub-canvas once per iteration and collects each declared output across the iterations: a declared
   * output `X` of stored type `T` is the node output `X: Collection<T?>` (`Collection<T>` when `T` is already nullable). Only a
   * block that also supports declared outputs and declares exactly one sub-canvas sets it. */
  declaredOutputsCollect?: boolean
  configurationSchema?: BlockConfigField[]
  subCanvasNames?: string[]
  subCanvasInputs?: Record<string, BlockPort[]>
  subCanvasOutputs?: Record<string, BlockPort[]>
  /** Name of the configuration field holding a reference to another workflow this block runs. The editor
   * then takes the node's ports from that workflow's declared parameters instead of from this descriptor,
   * and treats the field as config-owned so no wire is offered for it. Resolving the reference is the
   * host's job — see `WorkflowReferenceResolver`; the library has no persistence and cannot know where
   * workflows live. */
  runsWorkflowFrom?: string | null
  /** The type parameters the block's port types may name as `$Name` (a catalogue port's `type` is then a declaration
   * term: `$T`, `Collection<$T?>`). Absent for a block that is not generic. */
  typeParameters?: BlockTypeParameter[]

  /** Branch sets that each independently satisfy a declared output, for blocks whose runtime merges
   * branches rather than taking exactly one. A try/catch block declares `[['Finally'], ['Try', 'Catch']]`.
   * Absent means the default: every branch must set every declared output. */
  outputSatisfyingBranchSets?: string[][]
  [key: string]: unknown
}

/** The term of a node's declared output `port` as the node offers it: `Collection<T?>` on a collecting block (the stored type
 * `T` is the body's exit term), else the stored type. The editor grades and shows the output by this term. */
export function declaredOutputTerm(blockType: Pick<BlockTypeDescriptor, 'declaredOutputsCollect'>, port: { type: string }): string {
  return blockType.declaredOutputsCollect === true ? collectedPortType(port.type) : port.type
}
