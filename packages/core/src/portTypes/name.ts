/** The vocabulary of port-type names known at COMPILE time. The package seeds it with the built-ins; a host
 * adds its own by declaration merging, which is how an open runtime vocabulary still yields editor
 * autocomplete:
 *
 *   declare module '@reeve-iot/workflow-core' {
 *     interface PortTypeVocabulary { Device: true; Room: true }
 *   }
 *
 * Augmenting is optional. Skipping it costs autocomplete, never correctness — the runtime answer always
 * comes from the PortTypeRegistry, never from this interface. */
export interface PortTypeVocabulary {
  String: true
  Number: true
  Int: true
  Boolean: true
  DateTime: true
  Json: true
}

/** A port-type name. Known names autocomplete; any other string is still accepted, because a graph may
 * legitimately carry a type this build has never heard of (saved by a newer host, or a plugin's own type).
 * `Record<never, never>` is the empty-object intersection that keeps literal suggestions alive without
 * tripping the no-empty-object-type lint. */
export type PortTypeName = keyof PortTypeVocabulary | (string & Record<never, never>)
