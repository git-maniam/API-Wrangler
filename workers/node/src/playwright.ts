import { writeFileSync, mkdirSync } from "node:fs";
import { dirname } from "node:path";

export interface CapturedTraffic {
  id: string;
  scenarioName: string;
  timestamp: string;
  method: string;
  url: string;
  origin: string;
  path: string;
  query?: string;
  headers: Record<string, string>;
  body?: string;
  responseStatusCode: number;
  responseHeaders: Record<string, string>;
  responseBody?: string;
  durationMs: number;
  isCached: boolean;
  isServiceWorker: boolean;
  isWebSocket: boolean;
  isTruncated: boolean;
}

export interface InterceptorOptions {
  scenarioName: string;
  outputPath?: string;
  maxBodyBytes?: number;
  redactSecrets?: boolean;
  urlFilter?: (url: string) => boolean;
}

const SENSITIVE_HEADERS = new Set([
  "authorization",
  "proxy-authorization",
  "cookie",
  "set-cookie",
  "x-api-key",
  "apikey",
  "token",
  "x-auth-token",
  "secret"
]);

const SENSITIVE_FIELDS = new Set([
  "password",
  "secret",
  "token",
  "accesstoken",
  "refreshtoken",
  "apikey",
  "clientsecret",
  "credential",
  "privatekey"
]);

export class PlaywrightTrafficCollector {
  private records: CapturedTraffic[] = [];
  private requestTimings = new Map<string, number>();
  private readonly options: Required<InterceptorOptions>;

  constructor(options: InterceptorOptions) {
    this.options = {
      scenarioName: options.scenarioName,
      outputPath: options.outputPath ?? ".api-wrangler/captures/playwright-traffic.json",
      maxBodyBytes: options.maxBodyBytes ?? 262_144, // 256 KiB
      redactSecrets: options.redactSecrets ?? true,
      urlFilter: options.urlFilter ?? (() => true)
    };
  }

  public getRecords(): CapturedTraffic[] {
    return [...this.records];
  }

  public clear(): void {
    this.records = [];
    this.requestTimings.clear();
  }

  public handleRequest(request: {
    url(): string;
    method(): string;
    headers(): Record<string, string>;
    postData(): string | null;
  }): void {
    const url = request.url();
    if (!this.options.urlFilter(url)) return;
    this.requestTimings.set(url, Date.now());
  }

  public async handleResponse(response: {
    url(): string;
    status(): number;
    headers(): Record<string, string>;
    text(): Promise<string>;
    fromServiceWorker?(): boolean;
    request(): {
      method(): string;
      headers(): Record<string, string>;
      postData(): string | null;
    };
  }): Promise<void> {
    const url = response.url();
    if (!this.options.urlFilter(url)) return;

    const startTime = this.requestTimings.get(url) ?? Date.now();
    const durationMs = Math.max(0, Date.now() - startTime);
    const parsedUrl = new URL(url);

    let isTruncated = false;
    let reqBody = response.request().postData() ?? undefined;
    if (reqBody && reqBody.length > this.options.maxBodyBytes) {
      reqBody = reqBody.slice(0, this.options.maxBodyBytes) + " [TRUNCATED]";
      isTruncated = true;
    }
    if (reqBody && this.options.redactSecrets) {
      reqBody = this.redactJsonPayload(reqBody);
    }

    let resBody: string | undefined;
    try {
      resBody = await response.text();
      if (resBody && resBody.length > this.options.maxBodyBytes) {
        resBody = resBody.slice(0, this.options.maxBodyBytes) + " [TRUNCATED]";
        isTruncated = true;
      }
      if (resBody && this.options.redactSecrets) {
        resBody = this.redactJsonPayload(resBody);
      }
    } catch {
      // Stream, redirect, or connection closed
    }

    const reqHeaders = this.redactHeaders(response.request().headers());
    const resHeaders = this.redactHeaders(response.headers());

    const isServiceWorker = typeof response.fromServiceWorker === "function" ? response.fromServiceWorker() : false;
    const isCached = response.status() === 304 || (resHeaders["x-from-cache"] !== undefined);
    const isWebSocket = reqHeaders["upgrade"]?.toLowerCase() === "websocket";

    const record: CapturedTraffic = {
      id: `cap-${Math.random().toString(36).substring(2, 10)}`,
      scenarioName: this.options.scenarioName,
      timestamp: new Date().toISOString(),
      method: response.request().method(),
      url,
      origin: parsedUrl.origin,
      path: parsedUrl.pathname,
      query: parsedUrl.search || undefined,
      headers: reqHeaders,
      body: reqBody,
      responseStatusCode: response.status(),
      responseHeaders: resHeaders,
      responseBody: resBody,
      durationMs,
      isCached,
      isServiceWorker,
      isWebSocket,
      isTruncated
    };

    this.records.push(record);
  }

  public saveToFile(): void {
    const dir = dirname(this.options.outputPath);
    mkdirSync(dir, { recursive: true });
    writeFileSync(this.options.outputPath, JSON.stringify(this.records, null, 2), "utf8");
  }

  private redactHeaders(headers: Record<string, string>): Record<string, string> {
    if (!this.options.redactSecrets) return { ...headers };
    const out: Record<string, string> = {};
    for (const [k, v] of Object.entries(headers)) {
      out[k] = SENSITIVE_HEADERS.has(k.toLowerCase()) ? "[REDACTED]" : v;
    }
    return out;
  }

  private redactJsonPayload(text: string): string {
    try {
      const obj = JSON.parse(text);
      this.redactObjectProperties(obj);
      return JSON.stringify(obj);
    } catch {
      return text;
    }
  }

  private redactObjectProperties(obj: unknown): void {
    if (!obj || typeof obj !== "object") return;
    if (Array.isArray(obj)) {
      for (const item of obj) this.redactObjectProperties(item);
      return;
    }
    for (const [key, value] of Object.entries(obj as Record<string, unknown>)) {
      if (SENSITIVE_FIELDS.has(key.toLowerCase())) {
        (obj as Record<string, unknown>)[key] = "[REDACTED]";
      } else if (typeof value === "object" && value !== null) {
        this.redactObjectProperties(value);
      }
    }
  }
}

/**
 * Attaches the API Wrangler traffic collector to a Playwright Page object.
 */
export function attachApiWranglerCollector(page: any, options: InterceptorOptions): PlaywrightTrafficCollector {
  const collector = new PlaywrightTrafficCollector(options);
  page.on("request", (req: any) => collector.handleRequest(req));
  page.on("response", (res: any) => void collector.handleResponse(res));
  return collector;
}

