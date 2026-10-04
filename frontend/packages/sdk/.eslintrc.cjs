module.exports = {
  root: true,
  extends: [require.resolve('@learnstack/config/eslint')],
  parserOptions: { project: ['./tsconfig.json'], tsconfigRootDir: __dirname },
  ignorePatterns: ['src/generated/'],
};
