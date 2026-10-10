import type { NextConfig } from 'next';
import createNextIntlPlugin from 'next-intl/plugin';

const nextConfig: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
  images: { unoptimized: true },
  transpilePackages: ['@learnstack/ui', '@learnstack/sdk'],
  skipMiddlewareUrlNormalize: true,
  skipTrailingSlashRedirect: true,
  experimental: { serverComponentsHmrCache: false },
};

export default createNextIntlPlugin('./src/i18n/request.ts')(nextConfig);
