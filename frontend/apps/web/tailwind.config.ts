import type { Config } from 'tailwindcss';

import { learnstackTailwindPreset } from '@learnstack/config/tailwind';

const config: Config = {
  presets: [learnstackTailwindPreset],
  content: ['./src/**/*.{ts,tsx,mdx}'],
};

export default config;
