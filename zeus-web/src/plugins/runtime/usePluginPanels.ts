import { useEffect, useState } from 'react';
import { listRegisteredPanels, subscribe, type RegisteredPluginPanel } from './pluginRuntime';

export function usePluginPanels(): RegisteredPluginPanel[] {
  const [panels, setPanels] = useState<RegisteredPluginPanel[]>(() => listRegisteredPanels());
  useEffect(() => subscribe(() => setPanels(listRegisteredPanels())), []);
  return panels;
}
