// Runs suite.ts in the VS Code installed on this machine, with a profile and extensions folder of its own so
// nothing of yours is touched. It opens a window while it runs.
//
// VSCODE_PATH is VS Code's executable, if it isn't in the usual place; MEEP_PATH is the meep to test, if not the one
// built in src/Meep2.Cli. The test is skipped when there's no VS Code: it never downloads one.
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import { join, resolve } from "path";
import { runTests } from "@vscode/test-electron";

const root = resolve(__dirname, "..", "..");
const usual =
  process.platform === "darwin"
    ? ["/Applications/Visual Studio Code.app/Contents/MacOS/Code", "/Applications/Visual Studio Code.app/Contents/MacOS/Electron"]
    : [];
const code = process.env.VSCODE_PATH ?? usual.find(existsSync);
const meep = process.env.MEEP_PATH ??
  resolve(root, "..", "..", "src", "Meep2.Cli", "bin", "Debug", "net11.0", process.platform === "win32" ? "meep.exe" : "meep");

async function main(): Promise<void> {
  if (!code || !existsSync(code)) {
    console.log("Skipped: there's no VS Code here. Set VSCODE_PATH to its executable.");
    return;
  }
  if (!existsSync(meep)) {
    console.error(`There's no meep at ${meep}. Build it with dotnet build, or set MEEP_PATH.`);
    process.exitCode = 1;
    return;
  }

  const scratch = mkdtempSync(join(tmpdir(), "meeplang-test-"));
  try {
    const workspace = join(scratch, "workspace");
    const user = join(scratch, "user");
    mkdirSync(workspace);
    mkdirSync(join(user, "User"), { recursive: true });
    writeFileSync(join(user, "User", "settings.json"), JSON.stringify({
      "meeplang.meepPath": meep,
      "telemetry.telemetryLevel": "off",
      "update.mode": "none",
      "workbench.startupEditor": "none",
    }));
    await runTests({
      vscodeExecutablePath: code,
      extensionDevelopmentPath: root,
      extensionTestsPath: join(__dirname, "suite"),
      launchArgs: [
        workspace,
        "--disable-extensions",
        `--user-data-dir=${user}`,
        `--extensions-dir=${join(scratch, "extensions")}`,
        "--disable-workspace-trust",
        "--skip-welcome",
        "--skip-release-notes",
      ],
    });
  } catch (error) {
    console.error(error);
    process.exitCode = 1;
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

void main();
