import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
  reactStrictMode: true,
  transpilePackages: ['@learnstack/ui', '@learnstack/sdk'],
};

export default nextConfig;
