import { learnstackTailwindPreset } from '@learnstack/config/tailwind';
import type { Config } from 'tailwindcss';

const config: Config = {
  presets: [learnstackTailwindPreset],
  content: ['./src/**/*.{ts,tsx,mdx}'],
};

export default config;
