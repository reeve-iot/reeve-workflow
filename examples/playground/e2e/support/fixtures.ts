import { test as base } from '@playwright/test'
import { readOnlyApi } from './api-client'
import type { ReadOnlyApi } from './api-client'
import { readHarnessState } from './state'
import type { HarnessState } from './state'

export interface Harness {
  readonly state: HarnessState
  readonly api: ReadOnlyApi
  readonly pageUrl: string
}

export const test = base.extend<{ harness: Harness }>({
  harness: async ({}, use) => {
    const state = readHarnessState()
    await use({
      state,
      api: readOnlyApi(),
      pageUrl: `${state.baseUrl}/?operator=${state.operatorId}`,
    })
  },
})

export { expect } from '@playwright/test'
