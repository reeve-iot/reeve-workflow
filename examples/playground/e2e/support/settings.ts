import type { Page } from '@playwright/test'

export async function openWorkflowSettings(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Settings', exact: true }).click()
}
