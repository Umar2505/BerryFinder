import type { IkonUiModuleLoader, IkonUiRegistry } from '@ikonai/sdk-react-ui';
import { createBerryMapResolver } from './berry-map';

export const loadBerryMapModule: IkonUiModuleLoader = () => [createBerryMapResolver()];

export function registerBerryMapModule(registry: IkonUiRegistry): void {
  registry.registerModule('berry-map', loadBerryMapModule);
}
