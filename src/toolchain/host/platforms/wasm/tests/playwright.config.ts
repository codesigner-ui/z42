import { defineConfig, devices } from '@playwright/test';

// Serve the wasm/ root (= parent dir of tests/) so the page can import
// pkg-web/ and js/ via relative URLs. http-server with -c-1 disables
// caching so re-running ./build.sh + ./test.sh sees fresh artifacts.
//
// Spec: docs/spec/archive/2026-05-12-add-wasm-tests/
const PORT = 4242;

export default defineConfig({
    testDir: '.',
    fullyParallel: false,
    workers: 1,
    use: {
        baseURL: `http://localhost:${PORT}`,
        actionTimeout: 10_000,
    },
    projects: [
        { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    ],
    webServer: {
        command: `npx http-server .. -p ${PORT} -c-1 --cors -s`,
        port: PORT,
        reuseExistingServer: !process.env.CI,
        timeout: 30_000,
    },
    reporter: [['list']],
});
