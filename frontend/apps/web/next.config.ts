import type { NextConfig } from 'next';
import createNextIntlPlugin from 'next-intl/plugin';

const nextConfig: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
  // Callback/logout queries must never enter automatic request/fetch logs.
  // The native launcher separately closes the pinned development trace sink.
  logging: false,
  images: { unoptimized: true },
  transpilePackages: ['@learnstack/ui', '@learnstack/sdk'],
  skipMiddlewareUrlNormalize: true,
  skipTrailingSlashRedirect: true,
  experimental: { serverComponentsHmrCache: false },
  webpack(configuration) {
    // Shared native/Next modules use NodeNext .js specifiers for emitted files.
    configuration.resolve.extensionAlias = {
      ...configuration.resolve.extensionAlias,
      '.js': ['.ts', '.tsx', '.js'],
    };
    return configuration;
  },
};

export default createNextIntlPlugin('./src/i18n/request.ts')(nextConfig);
