// The MCP SDK deliberately filters inherited environment. Pass only test selectors explicitly.
export const desktopTestEnv = () => Object.fromEntries(['AGY_NATIVE_EXECUTABLE', 'AGY_DESKTOP_TEST_SCOPE', 'AGY_CHROME_CDP_TEST_ENDPOINT'].filter(key => process.env[key]).map(key => [key, process.env[key]]));
