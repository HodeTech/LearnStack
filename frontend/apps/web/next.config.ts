import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
  reactStrictMode: true,
  transpilePackages: ['@learnstack/ui', '@learnstack/sdk'],
  skipMiddlewareUrlNormalize: true,
  skipTrailingSlashRedirect: true,
  experimental: { serverComponentsHmrCache: false },
};

export default nextConfig;
