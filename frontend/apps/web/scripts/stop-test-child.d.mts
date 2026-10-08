import type { ChildProcess } from 'node:child_process';

export function stopTestChild(child: ChildProcess): Promise<void>;
