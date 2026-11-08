import { createRequire } from "node:module";
import { resolve } from "node:path";
import * as http from "node:http";
import * as https from "node:https";
import { URL } from "node:url";

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
  nextRequest?: string | null;
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
    let nextRequest: string | null = null;

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
        },
        clear(): void {
          for (const k of store.keys()) {
            variableMutations.push({ scope: name, key: k, value: null, action: "unset" });
          }
          store.clear();
        },
        toObject(): Record<string, any> {
          return Object.fromEntries(store);
        }
      };
    }

    const variables = createScope("variables", req.variables);
    const environment = createScope("environment", req.environment);
    const collectionVariables = createScope("collectionVariables", req.collectionVariables);
    const iterationDataMap = new Map<string, any>(Object.entries(req.iterationData || {}));
    const iterationData = {
      get(key: string) { return iterationDataMap.get(key); },
      has(key: string) { return iterationDataMap.has(key); },
      toObject() { return Object.fromEntries(iterationDataMap); }
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

    // Chai-compatible Expect assertion builder
    function createExpect(actual: any, inverted = false): any {
      const chain: any = {
        get not() {
          return createExpect(actual, !inverted);
        },
        get to() { return chain; },
        get be() { return chain; },
        get been() { return chain; },
        get is() { return chain; },
        get that() { return chain; },
        get which() { return chain; },
        get and() { return chain; },
        get has() { return chain; },
        get have() { return chain; },
        get with() { return chain; },
        get at() { return chain; },
        get of() { return chain; },
        get same() { return chain; },
        get deep() { return chain; },

        equal(expected: any) {
          const pass = actual === expected;
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${JSON.stringify(actual)} ${inverted ? "not " : ""}to equal ${JSON.stringify(expected)}`);
          }
        },
        eql(expected: any) {
          const pass = JSON.stringify(actual) === JSON.stringify(expected);
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${JSON.stringify(actual)} ${inverted ? "not " : ""}to deeply equal ${JSON.stringify(expected)}`);
          }
        },
        ok() {
          const pass = Boolean(actual);
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${JSON.stringify(actual)} ${inverted ? "not " : ""}to be truthy`);
          }
        },
        true() {
          const pass = actual === true;
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${actual} ${inverted ? "not " : ""}to be true`);
          }
        },
        false() {
          const pass = actual === false;
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${actual} ${inverted ? "not " : ""}to be false`);
          }
        },
        null() {
          const pass = actual === null;
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${actual} ${inverted ? "not " : ""}to be null`);
          }
        },
        undefined() {
          const pass = actual === undefined;
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${actual} ${inverted ? "not " : ""}to be undefined`);
          }
        },
        property(name: string, value?: any) {
          const hasProp = actual !== null && actual !== undefined && typeof actual === "object" && name in actual;
          if (inverted ? hasProp : !hasProp) {
            throw new Error(`Expected object ${inverted ? "not " : ""}to have property '${name}'`);
          }
          if (arguments.length > 1) {
            const valPass = JSON.stringify(actual[name]) === JSON.stringify(value);
            if (inverted ? valPass : !valPass) {
              throw new Error(`Expected property '${name}' ${inverted ? "not " : ""}to equal ${JSON.stringify(value)}, but found ${JSON.stringify(actual[name])}`);
            }
          }
        },
        include(item: any) {
          let pass = false;
          if (typeof actual === "string" || Array.isArray(actual)) {
            pass = actual.includes(item);
          } else if (actual && typeof actual === "object") {
            pass = Object.values(actual).includes(item) || (typeof item === "string" && item in actual);
          }
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${JSON.stringify(actual)} ${inverted ? "not " : ""}to include ${JSON.stringify(item)}`);
          }
        },
        contain(item: any) {
          return chain.include(item);
        },
        above(n: number) {
          const pass = actual > n;
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${actual} ${inverted ? "not " : ""}to be above ${n}`);
          }
        },
        below(n: number) {
          const pass = actual < n;
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${actual} ${inverted ? "not " : ""}to be below ${n}`);
          }
        },
        least(n: number) {
          const pass = actual >= n;
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${actual} ${inverted ? "not " : ""}to be at least ${n}`);
          }
        },
        most(n: number) {
          const pass = actual <= n;
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${actual} ${inverted ? "not " : ""}to be at most ${n}`);
          }
        },
        match(pattern: RegExp | string) {
          const re = typeof pattern === "string" ? new RegExp(pattern) : pattern;
          const pass = re.test(String(actual));
          if (inverted ? pass : !pass) {
            throw new Error(`Expected '${actual}' ${inverted ? "not " : ""}to match ${pattern}`);
          }
        },
        a(expectedType: string) {
          let pass = false;
          if (expectedType === "array") pass = Array.isArray(actual);
          else if (expectedType === "null") pass = actual === null;
          else pass = typeof actual === expectedType;
          if (inverted ? pass : !pass) {
            throw new Error(`Expected ${JSON.stringify(actual)} ${inverted ? "not " : ""}to be a ${expectedType}`);
          }
        },
        an(expectedType: string) {
          return chain.a(expectedType);
        },
        // Direct wrangler method compatibility aliases
        toBe(expected: any) { return chain.equal(expected); },
        toEqual(expected: any) { return chain.eql(expected); },
        toBeTruthy() { return chain.ok(); },
        toBeFalsy() { return chain.not.ok(); },
        toBeNull() { return chain.null(); },
        toBeDefined() { return chain.not.undefined(); },
        toBeUndefined() { return chain.undefined(); },
        toContain(item: any) { return chain.include(item); },
        toBeGreaterThan(n: number) { return chain.above(n); },
        toBeLessThan(n: number) { return chain.below(n); },
        toBeGreaterThanOrEqual(n: number) { return chain.least(n); },
        toBeLessThanOrEqual(n: number) { return chain.most(n); },
        toMatch(pattern: RegExp | string) { return chain.match(pattern); }
      };

      return chain;
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

    // Helper to make secondary HTTP requests (pm.sendRequest)
    function executeSecondaryRequest(
      options: string | { url: string; method?: string; headers?: Record<string, string>; body?: string },
      callback?: (error: Error | null, response: any) => void
    ): Promise<any> {
      return new Promise((resolvePromise, rejectPromise) => {
        try {
          const targetUrl = typeof options === "string" ? options : options.url;
          const parsed = new URL(targetUrl);
          const method = typeof options === "string" ? "GET" : (options.method || "GET");
          const headers = typeof options === "string" ? {} : (options.headers || {});
          const body = typeof options === "string" ? undefined : options.body;

          const client = parsed.protocol === "https:" ? https : http;
          const clientReq = client.request(parsed, { method, headers }, (clientRes) => {
            let resBody = "";
            clientRes.setEncoding("utf8");
            clientRes.on("data", chunk => { resBody += chunk; });
            clientRes.on("end", () => {
              const resHeaders: Record<string, string> = {};
              for (const [k, v] of Object.entries(clientRes.headers)) {
                if (v !== undefined) resHeaders[k.toLowerCase()] = Array.isArray(v) ? v.join(", ") : v;
              }
              const responseObj = {
                code: clientRes.statusCode || 0,
                status: clientRes.statusMessage || "",
                headers: {
                  get: (name: string) => resHeaders[name.toLowerCase()],
                  has: (name: string) => name.toLowerCase() in resHeaders
                },
                text: () => resBody,
                json: () => JSON.parse(resBody)
              };

              if (callback) callback(null, responseObj);
              resolvePromise(responseObj);
            });
          });

          clientReq.on("error", (err) => {
            if (callback) callback(err, null);
            rejectPromise(err);
          });

          if (body) clientReq.write(body);
          clientReq.end();
        } catch (ex: any) {
          if (callback) callback(ex, null);
          rejectPromise(ex);
        }
      });
    }

    // Postman `pm.*` adapter
    const pm = {
      test: wrangler.test,
      expect: createExpect,

      variables: {
        get(key: string): any {
          if (iterationData.has(key)) return iterationData.get(key);
          if (environment.has(key)) return environment.get(key);
          if (collectionVariables.has(key)) return collectionVariables.get(key);
          return variables.get(key);
        },
        set(key: string, value: any): void {
          variables.set(key, value);
        },
        has(key: string): boolean {
          return iterationData.has(key) || environment.has(key) || collectionVariables.has(key) || variables.has(key);
        }
      },

      environment,
      collectionVariables,
      iterationData,

      request: {
        url: {
          toString() { return requestContext.url; },
          getPath() {
            try { return new URL(requestContext.url).pathname; } catch { return ""; }
          }
        },
        method: requestContext.method,
        headers: {
          get(name: string) {
            const lower = name.toLowerCase();
            for (const [k, v] of Object.entries(requestContext.headers)) {
              if (k.toLowerCase() === lower) return v;
            }
            return undefined;
          },
          has(name: string) {
            const lower = name.toLowerCase();
            return Object.keys(requestContext.headers).some(k => k.toLowerCase() === lower);
          }
        },
        body: {
          raw: requestContext.body,
          json() { return requestContext.json(); }
        }
      },

      response: responseContext ? {
        code: responseContext.status,
        status: responseContext.statusText,
        responseTime: responseContext.responseTime,
        responseSize: responseContext.size,
        headers: {
          get(name: string) {
            const lower = name.toLowerCase();
            for (const [k, v] of Object.entries(responseContext.headers)) {
              if (k.toLowerCase() === lower) return v;
            }
            return undefined;
          },
          has(name: string) {
            const lower = name.toLowerCase();
            return Object.keys(responseContext.headers).some(k => k.toLowerCase() === lower);
          }
        },
        text() { return responseContext.text(); },
        json() { return responseContext.json(); },
        to: {
          have: {
            status(code: number) {
              if (responseContext.status !== code) {
                throw new Error(`Expected response status code ${responseContext.status} to equal ${code}`);
              }
            },
            header(name: string, value?: string) {
              const lower = name.toLowerCase();
              let foundVal: string | undefined;
              for (const [k, v] of Object.entries(responseContext.headers)) {
                if (k.toLowerCase() === lower) { foundVal = v; break; }
              }
              if (foundVal === undefined) {
                throw new Error(`Expected response to have header '${name}'`);
              }
              if (value !== undefined && foundVal !== value) {
                throw new Error(`Expected response header '${name}' to equal '${value}', found '${foundVal}'`);
              }
            },
            jsonBody(path?: string) {
              const body = responseContext.json();
              if (path) {
                if (!(path in body)) throw new Error(`Expected json body to have property '${path}'`);
              }
            }
          },
          be: {
            get ok() {
              if (responseContext.status < 200 || responseContext.status >= 300) {
                throw new Error(`Expected response status ${responseContext.status} to be 2xx (OK)`);
              }
              return true;
            },
            get success() { return this.ok; },
            get clientError() {
              if (responseContext.status < 400 || responseContext.status >= 500) {
                throw new Error(`Expected response status ${responseContext.status} to be 4xx (Client Error)`);
              }
              return true;
            },
            get serverError() {
              if (responseContext.status < 500 || responseContext.status >= 600) {
                throw new Error(`Expected response status ${responseContext.status} to be 5xx (Server Error)`);
              }
              return true;
            },
            get info() {
              if (responseContext.status < 100 || responseContext.status >= 200) {
                throw new Error(`Expected response status ${responseContext.status} to be 1xx (Informational)`);
              }
              return true;
            }
          }
        }
      } : null,

      sendRequest(options: any, callback?: any) {
        const p = executeSecondaryRequest(options, callback);
        asyncTests.push(p.then(() => { }));
        return p;
      },

      // Unsupported API traps producing clear, actionable errors
      get visualizer() {
        throw new Error("pm.visualizer is not supported in API Wrangler. Use the structured viewer tab in the workbench.");
      },
      get cookies() {
        throw new Error("pm.cookies programmatic jar is not supported. Use the request/response cookie inspector.");
      },
      get execution() {
        return {
          skipRequest() {
            throw new Error("pm.execution.skipRequest is not supported. Configure folder/request lifecycle selection.");
          }
        };
      }
    };

    // Legacy Postman `postman.*` object
    const postman = {
      setNextRequest(target: string | null) {
        nextRequest = target;
      },
      setEnvironmentVariable(key: string, value: any) {
        environment.set(key, value);
      },
      getEnvironmentVariable(key: string) {
        return environment.get(key);
      },
      clearEnvironmentVariable(key: string) {
        environment.unset(key);
      },
      clearEnvironmentVariables() {
        environment.clear();
      },
      setGlobalVariable(key: string, value: any) {
        collectionVariables.set(key, value);
      },
      getGlobalVariable(key: string) {
        return collectionVariables.get(key);
      },
      clearGlobalVariable(key: string) {
        collectionVariables.unset(key);
      },
      clearGlobalVariables() {
        collectionVariables.clear();
      }
    };

    // Legacy `tests` dictionary
    const legacyTests: Record<string, boolean> = {};
    const testsProxy = new Proxy(legacyTests, {
      set(target, prop, value) {
        const name = String(prop);
        const passed = Boolean(value);
        target[name] = passed;
        testResults.push({ name, passed, error: passed ? undefined : `Legacy test '${name}' evaluated to false` });
        return true;
      }
    });

    // Support require from project path if specified
    const projectRequire = req.projectPath
      ? createRequire(resolve(req.projectPath, "package.json"))
      : defaultRequire;

    const context = {
      wrangler,
      pm,
      postman,
      tests: testsProxy,
      responseCode: responseContext ? { code: responseContext.status, name: responseContext.statusText } : null,
      responseBody: responseContext?.body || null,
      responseTime: responseContext?.responseTime || 0,
      responseHeaders: responseContext?.headers || {},
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
      nextRequest,
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
