import { spawn } from "node:child_process";
import { writeFileSync } from "node:fs";

// Feasibility probe only. No repository code, package installation, or arbitrary scripts run here.
let input = Buffer.alloc(0);
process.stdin.on("data", (chunk: Buffer) => {
  input = Buffer.concat([input, chunk]);
  if (input.length > 65_540) process.exit(2);
  if (input.length < 4) return;
  const length = input.readUInt32BE(0);
  if (length < 1 || length > 65_536) process.exit(2);
  if (input.length < length + 4) return;
  process.stdin.pause();
  void respond(input.subarray(4, length + 4));
});

async function respond(frame: Buffer): Promise<void> {
  try {
    const request = JSON.parse(frame.toString("utf8")) as {
      version: number; id: string; delayMs: number; childPidFile?: string;
    };
    if (request.version !== 1 || typeof request.id !== "string" ||
        !Number.isInteger(request.delayMs) || request.delayMs < 0) process.exit(2);
    let childPid: number | undefined;
    if (request.childPidFile) {
      const child = spawn(process.execPath, ["-e", "setInterval(() => {}, 1000)"], { stdio: "ignore" });
      childPid = child.pid;
      writeFileSync(request.childPidFile, String(childPid));
    }
    await new Promise(resolve => setTimeout(resolve, request.delayMs));
    if (childPid) process.kill(childPid);
    const body = Buffer.from(JSON.stringify({ version: 1, id: request.id, value: "ready", childPid }));
    const prefix = Buffer.alloc(4);
    prefix.writeUInt32BE(body.length);
    process.stdout.write(Buffer.concat([prefix, body]), () => process.exit(0));
  } catch { process.exit(2); }
}
