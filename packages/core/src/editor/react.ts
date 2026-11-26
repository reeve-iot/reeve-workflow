import { createContext, createElement, useContext, useState } from 'react'
import type { ReactNode } from 'react'
import { useStore } from 'zustand'
import type { PortTypeRegistry } from '../portTypes/types.js'
import type { ValidationMessageCatalogue } from './messageCatalogue.js'
import type { WorkflowEditorState, WorkflowEditorStore, WorkflowEditorStoreOptions } from './store.js'
import { createWorkflowEditorStore } from './store.js'

export const WorkflowEditorStoreContext = createContext<WorkflowEditorStore | null>(null)

/** The vocabulary the surrounding editor was configured with. Components read it to offer type choices,
 * which is what keeps a type picker showing the HOST's types rather than a list baked into the library. */
export const PortTypeRegistryContext = createContext<PortTypeRegistry | null>(null)
export const ValidationMessageCatalogueContext = createContext<ValidationMessageCatalogue | null>(null)

/** Read the host's validation message overrides from the editor provider. */
export function useValidationMessageCatalogue(): ValidationMessageCatalogue | undefined {
  return useContext(ValidationMessageCatalogueContext) ?? undefined
}

export function usePortTypes(): PortTypeRegistry {
  const registry = useContext(PortTypeRegistryContext)
  if (!registry) throw new Error('usePortTypes must be used inside a <WorkflowEditorStoreProvider>.')
  return registry
}

/** Owns one editor's store. Two editors on a page get two stores — there is deliberately no module-level
 * fallback, because a shared one leaks one document's unsaved canvas into the next. */
export function WorkflowEditorStoreProvider({
  options,
  children,
}: {
  options: WorkflowEditorStoreOptions
  children: ReactNode
}) {
  const [store] = useState(() => createWorkflowEditorStore(options))
  const portTypes = useStore(store, (state) => state.portTypes)
  return createElement(
    WorkflowEditorStoreContext.Provider,
    { value: store },
    createElement(PortTypeRegistryContext.Provider, { value: portTypes },
      createElement(ValidationMessageCatalogueContext.Provider, { value: options.validationMessages ?? null }, children)),
  )
}

/** The store object itself, for the rare reader that needs `getState()` outside a render — an effect
 * that must see the latest state without re-running whenever an unrelated field changes. */
export function useWorkflowEditorStoreApi(): WorkflowEditorStore {
  const store = useContext(WorkflowEditorStoreContext)
  if (!store) {
    throw new Error('useWorkflowEditorStoreApi must be used inside a <WorkflowEditorStoreProvider>.')
  }
  return store
}

/** Reads the editor store from context. Throws rather than falling back to a global, so a component
 * rendered outside the provider fails loudly instead of quietly editing nothing. */
export function useWorkflowEditorStore<T = WorkflowEditorState>(selector?: (state: WorkflowEditorState) => T): T {
  const store = useContext(WorkflowEditorStoreContext)
  if (!store) {
    throw new Error('useWorkflowEditorStore must be used inside a <WorkflowEditorStoreProvider>.')
  }
  const identity = (state: WorkflowEditorState) => state as unknown as T
  return useStore(store, selector ?? identity)
}
