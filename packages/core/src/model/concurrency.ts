/** How a workflow coordinates several trigger fires. These are engine concepts, not product ones: any
 * host that runs a graph more than once at a time has to answer the same question. */
export type WorkflowConcurrencyMode = 'Single' | 'Restart' | 'Queued' | 'Parallel'

/** What happens to a fire that arrives once the limit is reached. */
export type WorkflowOverflowBehavior = 'Drop' | 'Log' | 'Error'

export interface WorkflowConcurrency {
  mode: WorkflowConcurrencyMode
  maxConcurrentRuns: number
  maxQueuedRuns: number
  overflowBehavior: WorkflowOverflowBehavior
}
