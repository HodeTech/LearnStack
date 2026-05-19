module.exports = {
  root: true,
  extends: ['@learnstack/config/eslint', 'next/core-web-vitals'],
  parserOptions: {
    project: ['./tsconfig.json'],
    tsconfigRootDir: __dirname,
  },
};
