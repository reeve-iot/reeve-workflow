import type { PortInfo } from '../model/workflow.js'
import { readPortList } from '../model/storedDocument.js'

/** Reads the port list a block declared in its own configuration, through the stored-document reader: absent
 * or empty is no list, and a list the reader refuses yields no ports at all, never the entries it could read.
 * validatePortTypeNames reports that list, and a blank or absent type, as a finding; neither throws. */
export function parseDynamicPorts(json: string | null | undefined): PortInfo[] {
  if (json === null || json === undefined || typeof json !== 'string') return []
  const read = readPortList(json, 'input')
  if (!read.ok) return []
  return read.sites.map((site, i) => {
    const displayName = read.entries[i]?.['displayName']
    return {
      name: site.portName,
      displayName: typeof displayName === 'string' ? displayName : site.portName,
      isRequired: site.isRequired,
      type: site.rawType ?? '',
    }
  })
}
