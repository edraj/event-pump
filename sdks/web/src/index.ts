export {
  createEventPump,
  SDK_NAME,
  SDK_VERSION,
  type Attributes,
  type EpConfig,
  type EventPump,
  type Handles,
} from './client';

import { createEventPump } from './client';

/** Shared default instance for ESM consumers. */
export const ep = createEventPump();
export default ep;
