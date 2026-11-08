import { createRequire } from "node:module";
import { resolve } from "node:path";

const defaultRequire = createRequire(import.meta.url);

interface ExecuteRequest {
  version: number;
  id: string;
  action: "execute" | "ping";
  code?: string;
  stage?: "pre-request" | "test";
  projectPath?: string;
  timeoutMs?: number;
  request?: {
    url?: string;
    method?: string;
    headers?: Record<string, string>;
    body?: string;
  };
  response?: {
    status?: number;
    statusText?: string;
    headers?: Record<string, string>;
    body?: string;
    responseTime?: number;
    size?: number;
  };
  variables?: Record<string, any>;
  environment?: Record<string, any>;
  collectionVariables?: Record<string, any>;
  iterationData?: Record<string, any>;
}

interface TestResult {
  name: string;
  passed: boolean;
  error?: string;
}

interface VariableMutation {
  scope: "variables" | "environment" | "collectionVariables";
  key: string;
  value: any;
  action: "set" | "unset";
}

interface ConsoleLogEntry {
  level: "log" | "info" | "warn" | "error";
  message: string;
}

interface ExecuteResponse {
  version: number;
  id: string;
  success: boolean;
  outcome: "Passed" | "Failed" | "Error";
  testResults: TestResult[];
  variableMutations: VariableMutation[];
  consoleLogs: ConsoleLogEntry[];
  error?: string;
  durationMs: number;
}

let inputBuffer = Buffer.alloc(0);

process.stdin.on("data", (chunk: Buffer) => {
  inputBuffer = Buffer.concat([inputBuffer, chunk]);
  if (inputBuffer.length < 4) return;
  const length = inputBuffer.readUInt32BE(0);
  if (length < 1 || length > 16 * 1024 * 1024) process.exit(2);
  if (inputBuffer.length < length + 4) return;

  process.stdin.pause();
  const payload = inputBuffer.subarray(4, length + 4);
  void handleRequest(payload);
});

async function handleRequest(payload: Buffer): Promise<void> {
  const startTime = Date.now();
  let requestId = "unknown";

  try {
    const req = JSON.parse(payload.toString("utf8")) as ExecuteRequest;
    requestId = req.id;

    if (req.action === "ping") {
      sendResponse({
        version: 1,
        id: req.id,
        success: true,
        outcome: "Passed",
        testResults: [],
        variableMutations: [],
        consoleLogs: [],
        durationMs: Date.now() - startTime
      });
      return;
    }

    const testResults: TestResult[] = [];
    const variableMutations: VariableMutation[] = [];
    const consoleLogs: ConsoleLogEntry[] = [];
    let logBytes = 0;
    const maxLogBytes = 64 * 1024;

    function addLog(level: "log" | "info" | "warn" | "error", args: any[]) {
      const msg = args.map(a => typeof a === "object" ? JSON.stringify(a) : String(a)).join(" ");
      if (logBytes < maxLogBytes) {
        logBytes += Buffer.byteLength(msg, "utf8");
        consoleLogs.push({ level, message: msg });
      }
    }

    const customConsole = {
      log: (...args: any[]) => addLog("log", args),
      info: (...args: any[]) => addLog("info", args),
      warn: (...args: any[]) => addLog("warn", args),
      error: (...args: any[]) => addLog("error", args)
    };

    function createScope(name: "variables" | "environment" | "collectionVariables", initial?: Record<string, any> | null) {
      const store = new Map<string, any>(Object.entries(initial || {}));
      return {
        get(key: string): any {
          return store.get(key);
        },
        set(key: string, value: any): void {
          store.set(key, value);
          variableMutations.push({ scope: name, key, value, action: "set" });
        },
        has(key: string): boolean {
          return store.has(key);
        },
        unset(key: string): void {
          store.delete(key);
          variableMutations.push({ scope: name, key, value: null, action: "unset" });
        }
      };
    }

    const variables = createScope("variables", req.variables);
    const environment = createScope("environment", req.environment);
    const collectionVariables = createScope("collectionVariables", req.collectionVariables);
    const iterationDataMap = new Map<string, any>(Object.entries(req.iterationData || {}));
    const iterationData = {
      get(key: string) { return iterationDataMap.get(key); },
      has(key: string) { return iterationDataMap.has(key); }
    };

    const requestContext = {
      url: req.request?.url || "",
      method: req.request?.method || "GET",
      headers: { ...(req.request?.headers || {}) },
      body: req.request?.body || "",
      json() {
        return JSON.parse(this.body);
      },
      text() {
        return this.body;
      }
    };

    const responseContext = req.response ? {
      status: req.response.status || 0,
      statusText: req.response.statusText || "",
      headers: { ...(req.response.headers || {}) },
      body: req.response.body || "",
      responseTime: req.response.responseTime || 0,
      size: req.response.size || 0,
      json() {
        return JSON.parse(this.body);
      },
      text() {
        return this.body;
      }
    } : null;

    function createExpect(actual: any) {
      return {
        toBe(expected: any) {
          if (actual !== expected) {
            throw new Error(`Expected ${JSON.stringify(actual)} to be ${JSON.stringify(expected)}`);
          }
        },
        toEqual(expected: any) {
          if (JSON.stringify(actual) !== JSON.stringify(expected)) {
            throw new Error(`Expected ${JSON.stringify(actual)} to equal ${JSON.stringify(expected)}`);
          }
        },
        toBeTruthy() {
          if (!actual) throw new Error(`Expected ${JSON.stringify(actual)} to be truthy`);
        },
        toBeFalsy() {
          if (actual) throw new Error(`Expected ${JSON.stringify(actual)} to be falsy`);
        },
        toBeNull() {
          if (actual !== null) throw new Error(`Expected ${JSON.stringify(actual)} to be null`);
        },
        toBeDefined() {
          if (actual === undefined) throw new Error("Expected value to be defined, but was undefined");
        },
        toBeUndefined() {
          if (actual !== undefined) throw new Error(`Expected ${JSON.stringify(actual)} to be undefined`);
        },
        toContain(item: any) {
          if (typeof actual === "string" || Array.isArray(actual)) {
            if (!actual.includes(item)) throw new Error(`Expected ${JSON.stringify(actual)} to contain ${JSON.stringify(item)}`);
          } else {
            throw new Error(`toContain requires array or string, received ${typeof actual}`);
          }
        },
        toBeGreaterThan(n: number) {
          if (!(actual > n)) throw new Error(`Expected ${actual} > ${n}`);
        },
        toBeLessThan(n: number) {
          if (!(actual < n)) throw new Error(`Expected ${actual} < ${n}`);
        },
        toBeGreaterThanOrEqual(n: number) {
          if (!(actual >= n)) throw new Error(`Expected ${actual} >= ${n}`);
        },
        toBeLessThanOrEqual(n: number) {
          if (!(actual <= n)) throw new Error(`Expected ${actual} <= ${n}`);
        },
        toMatch(pattern: RegExp | string) {
          const re = typeof pattern === "string" ? new RegExp(pattern) : pattern;
          if (!re.test(String(actual))) throw new Error(`Expected '${actual}' to match pattern ${pattern}`);
        }
      };
    }

    const wrangler = {
      test(name: string, fn: () => void | Promise<void>) {
        try {
          const result = fn();
          if (result && typeof (result as any).then === "function") {
            asyncTests.push(
              (result as Promise<void>)
                .then(() => { testResults.push({ name, passed: true }); })
                .catch((err: any) => { testResults.push({ name, passed: false, error: err?.message || String(err) }); })
            );
          } else {
            testResults.push({ name, passed: true });
          }
        } catch (err: any) {
          testResults.push({ name, passed: false, error: err?.message || String(err) });
        }
      },
      expect: createExpect
    };

    const asyncTests: Promise<void>[] = [];

    // Support require from project path if specified
    const projectRequire = req.projectPath
      ? createRequire(resolve(req.projectPath, "package.json"))
      : defaultRequire;

    const context = {
      wrangler,
      request: requestContext,
      response: responseContext,
      variables,
      environment,
      collectionVariables,
      iterationData,
      console: customConsole,
      require: projectRequire,
      setTimeout,
      clearTimeout,
      setInterval,
      clearInterval
    };

    const scriptCode = req.code || "";
    const AsyncFunction = Object.getPrototypeOf(async function () { }).constructor;
    const runner = new AsyncFunction(...Object.keys(context), scriptCode);

    await runner(...Object.values(context));

    if (asyncTests.length > 0) {
      await Promise.all(asyncTests);
    }

    const hasFailed = testResults.some(t => !t.passed);
    sendResponse({
      version: 1,
      id: req.id,
      success: true,
      outcome: hasFailed ? "Failed" : "Passed",
      testResults,
      variableMutations,
      consoleLogs,
      durationMs: Date.now() - startTime
    });
  } catch (err: any) {
    sendResponse({
      version: 1,
      id: requestId,
      success: false,
      outcome: "Error",
      testResults: [],
      variableMutations: [],
      consoleLogs: [],
      error: err?.stack || err?.message || String(err),
      durationMs: Date.now() - startTime
    });
  }
}

function sendResponse(res: ExecuteResponse) {
  const body = Buffer.from(JSON.stringify(res), "utf8");
  const prefix = Buffer.alloc(4);
  prefix.writeUInt32BE(body.length);
  process.stdout.write(Buffer.concat([prefix, body]), () => {
    process.exit(0);
  });
}
