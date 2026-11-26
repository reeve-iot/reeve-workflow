import { createContext, useContext } from 'react'
import type { ReactNode } from 'react'
import type {
  BlockConfigField, BlockPort, BlockTypeDescriptor, BlockTypeParameter, PortTypeName, VariableMapping, WorkflowNode,
} from '@reeve-iot/workflow-core'

/** One entry of a node's configuration map. */
export type NodeConfigurationValue = WorkflowNode['configuration'][string]

/** What a host renderer is told about the configuration field it is being offered. */
export interface ConfigFieldContext {
  node: WorkflowNode
  blockType: BlockTypeDescriptor
  field: BlockConfigField
  /** The value stored on the node, exactly. A field's declared default is a hint the engine never applies,
   * so no default is folded in here and `field.defaultValue` is the only place to read one. */
  value: unknown
  /** Type of the block input sharing this field's name, when there is one. Hosts key their own editors
   * off this — a `Device` field wants a device picker, not a text box. */
  portType: PortTypeName | undefined
  /** What an incoming wire binds this field to, already rendered for display, or undefined when nothing
   * is wired. An edge mapping beats node configuration at run time, so a host that ignores this offers
   * an editable field whose value the engine discards. */
  edgeBinding: string | undefined
  /** Writes one field. */
  onChange: (value: NodeConfigurationValue) => void
  /** Writes the whole configuration, for a picker that sets several fields at once. */
  onConfigurationChange: (configuration: WorkflowNode['configuration']) => void
}

/** An input of the selected node fed by a literal on an incoming wire's mapping, which the panel draws with a value editor. */
export interface WireLiteralContext {
  node: WorkflowNode
  blockType: BlockTypeDescriptor
  input: BlockPort
  /** The wire whose mapping holds the literal. */
  edgeId: string
  /** The literal as stored on the mapping, or undefined when the mapping holds none. */
  value: unknown
  /** The term the literal is written in: the input's closed term, or its declared term while a type parameter is unbound. */
  portType: PortTypeName
  /** Writes the literal on the wire's mapping through the store's `updateEdgeMappings`; undefined removes the value. */
  onChange: (value: VariableMapping['literalValue'] | undefined) => void
}

export interface NodeSectionContext {
  node: WorkflowNode
  blockType: BlockTypeDescriptor
  onConfigurationChange: (configuration: WorkflowNode['configuration']) => void
}

/** What a host's type-argument picker is told about a node whose block declares type parameters. */
export interface TypeArgumentPickerContext {
  node: WorkflowNode
  blockType: BlockTypeDescriptor
  /** The parameters the block declares, each with the capability names a bound term must implement. */
  typeParameters: readonly BlockTypeParameter[]
  /** The node's `typeArguments` as stored: parameter name to term. Empty when it stores none. */
  typeArguments: Readonly<Record<string, string>>
  /** Writes the node's whole `typeArguments` through the store's `setNodeTypeArguments`; null, or a record with no entry,
   * removes the key. Whether a term fits its parameter is the server's to say. */
  onChange: (typeArguments: Record<string, string> | null) => void
}

/** A configuration value in Expression mode on a node whose wires offer `Json` outputs: where a host
 * draws its path field or type assist beside an expression such as `${previous.parse.Value}`. */
export interface ConfigProjectionControlContext {
  placement: 'configuration'
  node: WorkflowNode
  blockType: BlockTypeDescriptor
  /** The configuration field holding the expression. */
  fieldName: string
  /** The field's term: the input sharing its name, else the field's own type. */
  targetType: PortTypeName
  /** The `Json` and `Json?` outputs of the nodes wired into this one, host-answered ones included, as the mapping
   * dialog lists each wire's sources. */
  jsonOutputs: readonly { sourceNodeId: string; name: string; type: PortTypeName }[]
  /** The expression as stored, without its expression prefix. */
  expressionBody: string
  /** Replaces the expression body; the panel stores it as the field's value. */
  onExpressionChange: (body: string) => void
}

/** How a host reaches into the generated configuration form. Everything here is optional; without any of
 * it the panel renders exactly what the block type's schema declares. */
export interface NodeConfigCustomization {
  /** Called for each schema field before the panel renders it. Return a node to render that instead,
   * `null` to render nothing (a field the host owns elsewhere), or undefined to fall through. */
  renderConfigField?: (context: ConfigFieldContext) => ReactNode | null | undefined
  /** Called, while `NodeConfigPanel`'s `showWireLiterals` is on, for each input that an incoming wire feeds with a literal,
   * a schema field of the same name included. Return a node to render that instead of the panel's value editor, `null` to
   * render nothing (an input the host draws in its own section), or undefined to fall through. */
  renderWireLiteral?: (context: WireLiteralContext) => ReactNode | null | undefined
  /** Rendered after the generated fields — where a host puts pickers spanning several fields, or its own
   * per-block guidance. */
  renderNodeSections?: (context: NodeSectionContext) => ReactNode
  /** Called for a node whose block declares type parameters, after the generated fields. Return the host's picker
   * (it offers the terms the vocabulary registers for each parameter's constraints), or null for none. Without it the
   * panel draws no picker; `TypeArgumentPicker` is the library's, for a host to return here. */
  renderTypeArgumentPicker?: (context: TypeArgumentPickerContext) => ReactNode | null
  /** Called under a configuration field whose value is an expression, when the node's wires offer `Json` outputs.
   * Return the host's projection control, or null for none. Without it the panel draws no control. */
  renderProjectionControl?: (context: ConfigProjectionControlContext) => ReactNode | null
}

const NONE: NodeConfigCustomization = {}

export const NodeConfigCustomizationContext = createContext<NodeConfigCustomization>(NONE)

export function useNodeConfigCustomization(): NodeConfigCustomization {
  return useContext(NodeConfigCustomizationContext)
}
