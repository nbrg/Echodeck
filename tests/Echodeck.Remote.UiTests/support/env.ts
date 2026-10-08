/** Where the test host listens (see playwright.config.ts and TestHost/Program.cs). */
export const port = Number(process.env.ECHODECK_UI_PORT ?? 5899);
export const controlPort = Number(process.env.ECHODECK_UI_CONTROL_PORT ?? 5898);
export const appUrl = `http://127.0.0.1:${port}`;
export const controlUrl = `http://127.0.0.1:${controlPort}`;

/** The pairing token the fake PC accepts (FakeBackend.Token). */
export const token = '0123456789abcdef0123456789abcdef';
